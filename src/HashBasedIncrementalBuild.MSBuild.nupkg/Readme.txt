HashBasedIncrementalBuild.MSBuild Readme

You have successfully enabled hash-based incremental build in your project.

********************************************************************************
Note: currently this package works only with Visual C++ native projects. 
The underlying RewindTimeStamps task is language-agnostic, but the package
itself installs MSBuild targets that affect only ClInclude and ClCompile items.
Typically these are *.h, *.hpp, *.c and *.cpp source files.
********************************************************************************

Here's how standard incremental build works in MSBuild and why sometimes it is
not enough. For details see Incremental Builds on MSDN
(https://msdn.microsoft.com/en-us/library/ee264087.aspx).

Normally after the first build of a project, MSBuild limits the amount of work
needed for subsequent builds by comparing timestamps (last modified dates) of
input and output items of the targets that it executes. For example if an obj
file exists and its cpp source file is not newer than the obj, there is no need
to re-compile it. This often saves substantial build time.

Still it is possible that MSBuild does unnecessary work when the timestamp of
a source file changes but not the file itself. A typical scenario where it may
happen is when a source-versioning system (like Git) updates timestamps
during branch-switching. Consider an example in Git: you've got branch B
checked out, and your project is fresh built. B is a descendant of A and you
want to merge B into A. The familiar prcedure is to check out A and merge B.
You end up at the same commit as before, only A has caught up with B. Same
commit means same source files and that makes the previous build output
up-to-date. But when you run a build, MSBuild will still re-generate build
output. The reason is that during branch-switching Git has updated the
timestamps on all files that have changed between A and B and MSBuild considers
them "dirty". And the more B was different from A, the loger it will take to
complete the build.

This problem can be mitigated by taking advantage of hash-based incremental
building, that is a build where hashed contents of files are compared to decide
if a particular source file needs re-compilation. Unfortunatelly MSBuild (along
with many other building systems) do not support source file hashing.

That's where HashBasedIncrementalBuild.MSBuild comes into play. Here's how it
works:

1. The RewindTimestamp MSBuild task maintains a per-project database that holds
the last known timestamp and 64-bit hash value per each source file.

2. Before actual build happens, the RewindTimestampTask examines current source
files.

3. If a file does not have a timestamp-hash entry in the database, it is
created.

4. If a file has an entry and its current timestamp is not newer than the one
in the database, it is skipped.

5. If a file has an entry and its current timestamp is newer than the one
in the database, its current hash value is calculated and compared to the one
in the database. In they are the same, the file's timestamp is overwritten with
the persisted one, and that makes the file appear as if it has not been touched
at all. If the hash values differ, the database entry is updated with current
timestamp and hash value.

6. Any entries that do not have corresponding source files are deleted to
prevent uncontrolled database bloating as files are added and removed.

RewindTimestamp is executed as part of the Build target. On Clean the task will
delete its database.

The advantages of this tool are:
- it does not require any modifications to MSBuild,
- it runs in the background of every build without requiring any user input,
- it is configuration-free: just plug&play,
- build output is always up-to-date, that is actual changes on source files do
get re-compiled (although there is a very small, but non-zero chance of a hash
collision),
- it is fast

Now the cons:
- every "timestamp rewind" on an open file is noticed by Visual Studio and it
prompts the user to reload the file (annoying but harmless),
- the database file may need to be excluded from source control,
- the package has to be installed on every project separately, currently there
i no way to enable hash-based incremental building per-solution or globally

For testing or debugging purposes increase MSBuild's logging verbosity. This
will reveal additional details related to RewindTimestamps task.

3rd party software used in this project:
- LiteDB - Copyright (c) 2014-2015 Mauricio David, distributed under MIT
license, http://www.litedb.org
- xxHash - Copyright (c) 2012-2014, Yann Collet, distributed under BSD 2-Clause
license, http://cyan4973.github.io/xxHash/