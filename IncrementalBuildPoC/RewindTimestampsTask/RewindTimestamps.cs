using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Build.Utilities;
using Microsoft.Build.Framework;

namespace HashingMSBuild
{
    public class RewindTimestamps : Task
    {
        public override bool Execute()
        {
            foreach (var item in SourceFiles)
            {
                Log.LogMessage(MessageImportance.High, "Source file: {0}", item.ItemSpec);
            }

            return true;
        }

        [Required]
        public ITaskItem[] SourceFiles { get; set; }
    }
}
