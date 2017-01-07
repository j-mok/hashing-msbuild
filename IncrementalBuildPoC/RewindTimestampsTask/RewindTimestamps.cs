using LiteDB;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.Data.HashFunction;
using System.IO;
using System.Linq;

namespace HashingMSBuild
{
    public class RewindTimestamps : Task
    {
        public class SourceFileRecord
        {
            public SourceFileRecord()
            {
            }

            public int Id { get; set; }
            public string File { get; set; }
            public long LastWriteTimeUtc { get; set; }
            public long Hash { get; set; }
        }

        private xxHash hasher = new xxHash(hashSize: 64);

        public override bool Execute()
        {
            var currentSourceFiles = SourceFiles.ToDictionary(item => item.ItemSpec, item => File.GetLastWriteTimeUtc(item.ItemSpec));
            ProcessSourceFiles(currentSourceFiles);

            return true;
        }

        private void ProcessSourceFiles(Dictionary<string, DateTime> currentSourceFiles)
        {
            string databaseFile = GetDatabasePath();
            Log.LogMessage(MessageImportance.High, $"Using database {databaseFile}");

            using (var db = new LiteDatabase(databaseFile))
            {
                var storedFiles = db.GetCollection<SourceFileRecord>("files");

                Log.LogMessage(MessageImportance.High, $"Looking for updated time stamps...");
                foreach (var record in storedFiles.FindAll())
                {
                    if (!currentSourceFiles.ContainsKey(record.File))
                    {
                        Log.LogMessage(MessageImportance.High, $"Deleting the record associated with a no longer existing file {record.File}");
                        storedFiles.Delete(record.Id);
                        continue;
                    }

                    bool recordUpdated = CheckFileTimestamp(currentSourceFiles[record.File], record);
                    if (recordUpdated)
                    {
                        storedFiles.Update(record);
                    }

                    currentSourceFiles.Remove(record.File);
                }

                AddNewFileRecords(currentSourceFiles, storedFiles);
            }
        }

        private bool CheckFileTimestamp(DateTime currentTimestampUtc, SourceFileRecord record)
        {
            Log.LogMessage(MessageImportance.High, "Source file: {0}", record.File);
            Log.LogMessage(MessageImportance.High, string.Format(" ** Last write time [recorded]: {0:O}", DateTime.FromFileTime(record.LastWriteTimeUtc)));
            Log.LogMessage(MessageImportance.High, string.Format(" ** Last write time [current]: {0:O}", currentTimestampUtc.ToLocalTime()));
            Log.LogMessage(MessageImportance.High, string.Format(" ** xxHash (XXH64) [recorded]: {0:X8}", record.Hash));

            long currentLastWriteTimeUtc = currentTimestampUtc.ToFileTime();
            if (record.LastWriteTimeUtc >= currentLastWriteTimeUtc)
            {
                Log.LogMessage(MessageImportance.High, " ** Last write time not changed. Skipping.");
                return false;
            }

            var currentHash = ComputeHash(record.File);
            Log.LogMessage(MessageImportance.High, string.Format(" ** xxHash (XXH64) [current]: {0:X8}", currentHash));            

            if (currentHash != record.Hash)
            {
                Log.LogMessage(MessageImportance.High, " ** Hash value changed. Updating the record.");
                record.Hash = currentHash;
                record.LastWriteTimeUtc = currentLastWriteTimeUtc;
                return true;
            }
            else
            {
                Log.LogMessage(MessageImportance.High, " ** Hash value not changed. Rewinding last write time.");
                File.SetLastWriteTimeUtc(record.File, DateTime.FromFileTimeUtc(record.LastWriteTimeUtc));
                return false;
            }
        }

        private void AddNewFileRecords(Dictionary<string, DateTime> newSourceFiles, LiteCollection<SourceFileRecord> storedFiles)
        {
            int count = storedFiles.Insert(from entry in newSourceFiles
                                           let newFile = entry.Key
                                           let newFileTimestamp = entry.Value.ToFileTimeUtc()
                                           select new SourceFileRecord
                                           {
                                               File = newFile,
                                               LastWriteTimeUtc = newFileTimestamp,
                                               Hash = ComputeHash(newFile)
                                           });

            if (count > 0)
            {
                Log.LogMessage(MessageImportance.High, $"Added {count} new file record(s).");
            }
        }

        private string GetDatabasePath()
        {
            return Path.ChangeExtension(BuildEngine.ProjectFileOfTaskNode, "hashdb");
        }

        private long ComputeHash(string file)
        {
            using (var data = File.OpenRead(file))
            {
                byte[] hash = hasher.ComputeHash(data);
                return BitConverter.ToInt64(hash, 0);
            }
        }

        [Required]
        public ITaskItem[] SourceFiles { get; set; }
    }
}
