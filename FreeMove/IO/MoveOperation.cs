// FreeMove -- Move directories without breaking shortcuts or installations
//    Copyright(C) 2020  Luca De Martini

//    This program is free software: you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation, either version 3 of the License, or
//    (at your option) any later version.

//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
//    GNU General Public License for more details.

//    You should have received a copy of the GNU General Public License
//    along with this program.If not, see<http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FreeMove.IO
{
    /// <summary>
    /// Moves a folder or file. Across drives it copies (see <see cref="CopyOperation"/>), then
    /// deletes the original only after the copy completed and was verified. The journal, when
    /// given, records the phase so an interrupted move can be finished or rolled back.
    /// </summary>
    class MoveOperation : IOOperation
    {
        readonly string pathFrom;
        readonly string pathTo;
        readonly bool sameDrive;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        readonly CopyOperation innerCopy;

        /// <summary>The write-ahead record of this move, if any.</summary>
        public MoveJournal Journal { get; }

        /// <summary>Folders created to hold the destination; removed again if the move is rolled back.</summary>
        public List<string> CreatedParents { get; } = new List<string>();

        /// <summary>Fires after the copy is complete and verified, before the original is deleted.</summary>
        public event EventHandler CopyCompleted;

        public List<string> Warnings => innerCopy.Warnings;

        public MoveOperation(string pathFrom, string pathTo, bool createDestination, bool overwrite = false, MoveJournal journal = null, bool keepExisting = false)
        {
            this.pathFrom = pathFrom;
            this.pathTo = pathTo;
            Journal = journal;
            bool destinationExists = Directory.Exists(pathTo) || File.Exists(pathTo);
            // Merging into an existing destination always copies (a rename can't merge)
            sameDrive = !destinationExists &&
                        string.Equals(Path.GetPathRoot(pathFrom), Path.GetPathRoot(pathTo), StringComparison.OrdinalIgnoreCase);

            if (createDestination && !destinationExists)
            {
                try
                {
                    CreatedParents.AddRange(IOHelper.CreateDirectoryTracked(Path.GetDirectoryName(pathTo)));
                }
                catch (UnauthorizedAccessException)
                {
                    throw new UnauthorizedAccessException(Properties.Resources.ResourceManager.GetString("MoveOp_CreateDestUnauthorized"));
                }
                catch (IOException)
                {
                    throw new IOException(Properties.Resources.ResourceManager.GetString("MoveOp_CreateDestIOError"));
                }
            }
            innerCopy = new CopyOperation(pathFrom, pathTo) { Overwrite = overwrite, Journal = journal, KeepExistingFiles = keepExisting };
        }

        public override void Cancel()
        {
            cts.Cancel();
            innerCopy?.Cancel();
        }

        /// <summary>Removes the folders this move created to hold the destination, if they are empty.</summary>
        public void RemoveCreatedParents() => IOHelper.RemoveEmptyDirectories(CreatedParents);

        public override async Task Run()
        {
            innerCopy.ProgressChanged += (sender, e) => OnProgressChanged(e);
            innerCopy.Start += (sender, e) => OnStart(e);
            try
            {
                if (sameDrive)
                {
                    try
                    {
                        if (File.Exists(pathFrom))
                            await Task.Run(() => File.Move(pathFrom, pathTo), cts.Token);
                        else
                            await Task.Run(() => Directory.Move(pathFrom, pathTo), cts.Token);
                    }
                    catch (Exception e) when (!(e is OperationCanceledException))
                    {
                        throw new MoveFailedException(Properties.Resources.ResourceManager.GetString("MoveOp_MoveSameDriveFailed"), e);
                    }
                    // The rename already happened and can't fail halfway, so a journal write problem here
                    // must not be treated as a failed move (recovery recognises a finished rename anyway)
                    try { Journal?.SetPhase(JournalPhase.Delete); } catch { }
                    CopyCompleted?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    try
                    {
                        await innerCopy.Run();
                    }
                    catch (Exception e) when (!(e is OperationCanceledException)) // Wrap inner exceptions to signal which phase failed
                    {
                        throw new CopyFailedException(Properties.Resources.ResourceManager.GetString("MoveOp_CopyFailed"), e);
                    }

                    cts.Token.ThrowIfCancellationRequested();

                    // The copy is complete and verified: from here on, an interrupted move is
                    // finished rather than rolled back
                    Journal?.SetPhase(JournalPhase.Delete);
                    CopyCompleted?.Invoke(this, EventArgs.Empty);

                    try
                    {
                        await Task.Run(() => IOHelper.DeleteOriginal(pathFrom));
                    }
                    catch (Exception e)
                    {
                        throw new DeleteFailedException(Properties.Resources.ResourceManager.GetString("MoveOp_DeleteOldFailed"), e);
                    }
                }
            }
            finally
            {
                OnEnd(new EventArgs());
            }
        }

        public class MoveFailedException : Exception
        {
            public MoveFailedException(string message, Exception innerException) : base(message, innerException) { }
        }
        public class CopyFailedException : Exception
        {
            public CopyFailedException(string message, Exception innerException) : base(message, innerException) { }
        }
        public class DeleteFailedException : Exception
        {
            public DeleteFailedException(string message, Exception innerException) : base(message, innerException) { }
        }
    }
}
