using LiteDB;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.HashFunction;
using System.Diagnostics;
using System.IO;
using System.Linq;
using TPL = System.Threading.Tasks;

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
            ProcessSourceFiles(SourceFiles);

            sw.Stop();
            Log.LogMessage($"Timestamp database update took: {sw.Elapsed}");

            return true;
        }

        private void ProcessSourceFiles(ITaskItem[] sourceFiles)
        {
            string databaseFile = GetDatabasePath();
            Log.LogMessage($"Using database {databaseFile}");

            using (var db = new LiteDatabase(databaseFile))
            {
                // Phase 1: Load the whole database into memory and clean the db
                var records = db.GetCollection<SourceFileRecord>("files");
                var oldFilesInDb = records.FindAll().ToDictionary(r => r.File);
                db.DropCollection("files");

                records = db.GetCollection<SourceFileRecord>("files");
                var pendingHashUpdates = new List<SourceFileRecord>();

                // Phase 2: Write files whose timestamps have not changed back into the database
                Log.LogMessage(MessageImportance.Low, $"Looking for updated time stamps...");
                foreach (var sourceFile in sourceFiles)
                {
                    bool requiresHashRecalculation;
                    var record = CreateFileRecord(sourceFile, oldFilesInDb, out requiresHashRecalculation);

                    if (record != null)
                    {
                        if (requiresHashRecalculation)
                        {
                            pendingHashUpdates.Add(record);
                        }
                        else
                        {
                            records.Insert(record);
                        }
                    }
                }

                // Phase 3: Concurrently recalculate hash values, rewind timestamps of unchanged files and save updated (or new) entries in the db:
                TPL.Parallel.ForEach(pendingHashUpdates, record =>
                {
                    UpdateHashOrRewindTimestamp(record);
                });
                records.Insert(pendingHashUpdates.Where(r => r.File != null));
            }
        }

        private SourceFileRecord CreateFileRecord(ITaskItem sourceFileItem, IDictionary<string, SourceFileRecord> recordedFiles, out bool requiresHashRecalculation)
        {
            requiresHashRecalculation = false;
            Log.LogMessage(MessageImportance.Normal, "Source file: {0}", sourceFileItem.ItemSpec);

            var currentTimestampUtc = File.GetLastWriteTimeUtc(sourceFileItem.ItemSpec);
            Log.LogMessage(MessageImportance.Low, string.Format(" ** Last write time [current]: {0:O}", currentTimestampUtc.ToLocalTime()));

            if (!File.Exists(sourceFileItem.ItemSpec))
            {
                Log.LogMessage(MessageImportance.Low, $" ** File does not exist - it will not a have a timestamp record in the database.");
                return null;
            }

            var newRecord = new SourceFileRecord
            {
                File = sourceFileItem.ItemSpec
            };

            SourceFileRecord oldRecord;
            if (recordedFiles.TryGetValue(newRecord.File, out oldRecord))
            {
                var recordedTimestampUtc = DateTime.FromFileTimeUtc(oldRecord.LastWriteTimeUtc);
                Log.LogMessage(MessageImportance.Low, string.Format(" ** Last write time [recorded]: {0:O}", recordedTimestampUtc.ToLocalTime()));
                Log.LogMessage(MessageImportance.Low, string.Format(" ** xxHash (XXH64) [recorded]: {0:X8}", oldRecord.Hash));

                newRecord.Id = oldRecord.Id;
                newRecord.LastWriteTimeUtc = oldRecord.LastWriteTimeUtc;
                newRecord.Hash = oldRecord.Hash;

                if (recordedTimestampUtc < currentTimestampUtc)
                {
                    Log.LogMessage(MessageImportance.Normal, " ** Last write time changed. File requires hash value recalculation.");
                    requiresHashRecalculation = true;
                }
                else
                {
                    Log.LogMessage(MessageImportance.Normal, " ** Last write time not changed. Skipping.");
                }
            }
            else
            {
                Log.LogMessage(MessageImportance.Normal, " ** The file has no entry in the database - one will be created.");
                requiresHashRecalculation = true;
            }

            return newRecord;
        }

        private void UpdateHashOrRewindTimestamp(SourceFileRecord record)
        {
            try
            {
                long currentHash = ComputeHash(record.File);

                if (record.Id == 0)     // New record
                {
                    Log.LogMessage(MessageImportance.Low, string.Format("Computed hash value of {0}: {1:X8} - creating a new file entry.", record.File, currentHash));
                    record.LastWriteTimeUtc = File.GetLastWriteTimeUtc(record.File).ToFileTimeUtc();
                    record.Hash = currentHash;
                }
                else                    // Updated record
                {
                    if (currentHash != record.Hash)
                    {
                        Log.LogMessage(MessageImportance.Low, string.Format("Computed hash value of {0}: {1:X8} - hash value changed, updating entry.", record.File, currentHash));
                        record.LastWriteTimeUtc = File.GetLastWriteTimeUtc(record.File).ToFileTimeUtc();
                        record.Hash = currentHash;
                    }
                    else
                    {
                        Log.LogMessage(MessageImportance.Low, string.Format("Computed hash value of {0}: {1:X8} - hash value not changed - rewinding last write time.", record.File, currentHash));
                        File.SetLastWriteTimeUtc(record.File, DateTime.FromFileTimeUtc(record.LastWriteTimeUtc));
                    }
                }
            }
            catch (IOException exc)
            {
                Log.LogErrorFromException(exc);
                Log.LogMessage(MessageImportance.Low, $"File {record.File} won't have an entry in the database because an I/O error occurred when calculating hash value of the file.");
                record.File = null;     // Mark record as excluded
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
