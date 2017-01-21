using LiteDB;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.Data.HashFunction;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TPL=System.Threading.Tasks;

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

        [Required]
        public ITaskItem[] SourceFiles { get; set; }

        public string DatabasePath { get; set; }

        private xxHash hasher = new xxHash(hashSize: 64);

        public override bool Execute()
        {
            var sw = Stopwatch.StartNew();
            var currentSourceFiles = SourceFiles.ToDictionary(item => item.ItemSpec, item => File.GetLastWriteTimeUtc(item.ItemSpec));
            ProcessSourceFiles(currentSourceFiles);

            sw.Stop();
            Log.LogMessage(MessageImportance.High, $"Timestamp database update took: {sw.Elapsed}");

            return true;
        }

        private void ProcessSourceFiles(Dictionary<string, DateTime> currentSourceFiles)
        {
            string databaseFile = GetDatabasePath();
            Log.LogMessage($"Using database {databaseFile}");

            using (var db = new LiteDatabase(databaseFile))
            {
                var storedFiles = db.GetCollection<SourceFileRecord>("files");

                Log.LogMessage(MessageImportance.Low, $"Looking for updated time stamps...");
                TPL.Parallel.ForEach(storedFiles.FindAll(), record =>
                {
                    if (!currentSourceFiles.ContainsKey(record.File))
                    {
                        Log.LogMessage(MessageImportance.Low, $"Deleting the record associated with a no longer existing file {record.File}");
                        storedFiles.Delete(record.Id);
                        return;
                    }

                    if (!File.Exists(record.File))
                    {
                        Log.LogMessage(MessageImportance.Low, $"File {record.File} does not exist - it will not a have a timestamp record in the database.");
                        return;
                    }

                    try
                    {
                        bool recordUpdated = CheckFileTimestamp(currentSourceFiles[record.File], record);
                        if (recordUpdated)
                        {
                            storedFiles.Update(record);
                        }
                    }
                    catch (IOException exc)
                    {
                        Log.LogErrorFromException(exc);
                        Log.LogMessage(MessageImportance.Low, $"Deleting a record for {record.File} because an I/O error during record updating.");
                        storedFiles.Delete(record.Id);
                    }

                    currentSourceFiles.Remove(record.File);
                });

                AddNewFileRecords(currentSourceFiles, storedFiles);
            }
        }

        private bool CheckFileTimestamp(DateTime currentTimestampUtc, SourceFileRecord record)
        {
            Log.LogMessage(MessageImportance.Normal, "Source file: {0}", record.File);
            Log.LogMessage(MessageImportance.Low, string.Format(" ** Last write time [recorded]: {0:O}", DateTime.FromFileTime(record.LastWriteTimeUtc)));
            Log.LogMessage(MessageImportance.Low, string.Format(" ** Last write time [current]: {0:O}", currentTimestampUtc.ToLocalTime()));
            Log.LogMessage(MessageImportance.Low, string.Format(" ** xxHash (XXH64) [recorded]: {0:X8}", record.Hash));

            long currentLastWriteTimeUtc = currentTimestampUtc.ToFileTime();
            if (record.LastWriteTimeUtc >= currentLastWriteTimeUtc)
            {
                Log.LogMessage(MessageImportance.Normal, " ** Last write time not changed. Skipping.");
                return false;
            }

            var currentHash = ComputeHash(record.File);
            Log.LogMessage(MessageImportance.Low, string.Format(" ** xxHash (XXH64) [current]: {0:X8}", currentHash));

            if (currentHash != record.Hash)
            {
                Log.LogMessage(MessageImportance.Normal, " ** Hash value changed. Updating the record.");
                record.Hash = currentHash;
                record.LastWriteTimeUtc = currentLastWriteTimeUtc;
                return true;
            }
            else
            {
                Log.LogMessage(MessageImportance.Normal, " ** Hash value not changed. Rewinding last write time.");
                File.SetLastWriteTimeUtc(record.File, DateTime.FromFileTimeUtc(record.LastWriteTimeUtc));
                return false;
            }
        }

        private void AddNewFileRecords(Dictionary<string, DateTime> newSourceFiles, LiteCollection<SourceFileRecord> storedFiles)
        {
            int count = storedFiles.Insert(SafeCreateRecords(newSourceFiles));

            if (count > 0)
            {
                Log.LogMessage(MessageImportance.Normal, $"Added {count} new file record(s).");
            }
        }

        private IEnumerable<SourceFileRecord> SafeCreateRecords(Dictionary<string, DateTime> sourceFiles)
        {
            foreach (var entry in sourceFiles)
            {
                Log.LogMessage(MessageImportance.Low, $"Creating a timestamp record for file {entry.Key}");

                if (!File.Exists(entry.Key))
                {
                    Log.LogMessage(MessageImportance.Low, $"File {entry.Key} does not exist - it will not a have a timestamp record in the database.");
                    continue;
                }

                SourceFileRecord record;
                try
                {
                    record = new SourceFileRecord
                    {
                        File = entry.Key,
                        LastWriteTimeUtc = entry.Value.ToFileTimeUtc(),
                        Hash = ComputeHash(entry.Key)
                    };                    
                }
                catch (IOException exc)
                {
                    Log.LogErrorFromException(exc);
                    Log.LogMessage(MessageImportance.Low, $"Could not create a record for {entry.Key} because of an I/O error.");
                    continue;
                }

                yield return record;
            }
        }

        private string GetDatabasePath()
        {
            return DatabasePath ?? Path.ChangeExtension(BuildEngine.ProjectFileOfTaskNode, "hashdb");
        }

        private long ComputeHash(string file)
        {
            using (var data = File.OpenRead(file))
            {
                byte[] hash = hasher.ComputeHash(data);
                return BitConverter.ToInt64(hash, 0);
            }
        }
    }
}
