using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Utilities;
using Microsoft.Build.Framework;
using System.IO;
using LiteDB;
using System.Data.HashFunction;

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
            public long LastWriteTime { get; set; }
            public byte[] Hash { get; set; }
        }

        private xxHash hasher = new xxHash();

        public override bool Execute()
        {
            var currentSourceFiles = SourceFiles.ToDictionary(item => item.ItemSpec, item => File.GetLastWriteTime(item.ItemSpec));
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

                Log.LogMessage(MessageImportance.High, $"Checking for updated time stamps...");
                foreach (var record in storedFiles.FindAll())
                {
                    if (!currentSourceFiles.ContainsKey(record.File))
                    {
                        Log.LogMessage(MessageImportance.High, $"Deleting the record associated with a no longer existing file {record.File}");
                        storedFiles.Delete(record.Id);
                    }

                    Log.LogMessage(MessageImportance.High, "Source file: {0}", record.File);
                    Log.LogMessage(MessageImportance.High, " ** Last write time [recorded]: {0}", record.LastWriteTime);

                    var currentTimestamp = currentSourceFiles[record.File].ToFileTimeUtc();
                    currentSourceFiles.Remove(record.File);
                    Log.LogMessage(MessageImportance.High, " ** Last write time [current]: {0}", currentTimestamp);
                    Log.LogMessage(MessageImportance.High, " ** xxHash (XXH64) [recorded]: {0}", Convert.ToBase64String(record.Hash));

                    if (currentTimestamp > record.LastWriteTime)
                    {
                        var currentHash = ComputeHash(record.File);
                        Log.LogMessage(MessageImportance.High, " ** xxHash (XXH64) [current]: {0}", Convert.ToBase64String(currentHash));

                        bool hashChanged = false;

                        for (int i = 0; i < currentHash.Length; i++)
                        {
                            if (currentHash[i] != record.Hash[i])
                            {
                                hashChanged = true;
                                break;
                            }
                        }

                        if (hashChanged)
                        {
                            Log.LogMessage(MessageImportance.High, " ** Hash value changed. Updating the record.");
                            record.Hash = currentHash;
                            record.LastWriteTime = currentTimestamp;
                            storedFiles.Update(record);
                        }
                        else
                        {
                            Log.LogMessage(MessageImportance.High, " ** Hash value not changed. Rewinding last write time.");
                            File.SetLastWriteTime(record.File, DateTime.FromFileTimeUtc(record.LastWriteTime));                            
                        }
                    }
                    else
                    {
                        Log.LogMessage(MessageImportance.High, " ** Last write time not changed. Skipping.");
                    }
                }

                int count = storedFiles.Insert(from entry in currentSourceFiles
                                   let newFile = entry.Key
                                   let newFileTimestamp = entry.Value.ToFileTimeUtc()
                                   select new SourceFileRecord
                                   {
                                       File = newFile,
                                       LastWriteTime = newFileTimestamp,
                                       Hash = ComputeHash(newFile)
                                   });

                if (count > 0)
                {
                    Log.LogMessage(MessageImportance.High, $"Added {count} new file record(s).");
                }
            }
        }

        private string GetDatabasePath()
        {
            return Path.ChangeExtension(BuildEngine.ProjectFileOfTaskNode, "hashdb");
        }

        private byte[] ComputeHash(string file)
        {
            using (var data = File.OpenRead(file))
            {
                return hasher.ComputeHash(data);
            }
        }

        [Required]
        public ITaskItem[] SourceFiles { get; set; }
    }
}
