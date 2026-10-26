# FreeMove

[![Build Beta & Release](https://github.com/connorlabianco/FreeMove/actions/workflows/build-release.yml/badge.svg)](https://github.com/connorlabianco/FreeMove/actions/workflows/build-release.yml)

Move directories or files freely without breaking installations or shortcuts

You can use this tool to move programs that install on `C:\` by default to another drive to save space on your main drive

## Download

[![GitHub Release](https://img.shields.io/github/v/release/connorlabianco/FreeMove?style=for-the-badge&logo=github)](https://github.com/connorlabianco/FreeMove/releases)

**Which version should I download?**

| Version | Description | Requirement |
| :--- | :--- | :--- |
| **.NET 8 (Standard)** | Suitable for users with .NET 8 installed. Smallest size. | Requires [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| **.NET 8 (SelfContained)** | Best for portability. Works without installing .NET. | None |
| **.NET 4.8** | Suitable for users who do not want to install .NET 8 or are using Windows 7/8 | Requires .NET Framework 4.8 (Built-in on Win10/11) |

*Note: Choose the architecture (`win-x64`, `win-x86`, or `win-arm64`) that matches your system.*

<!-- ### From Scoop

```
scoop install freemove
``` -->

## How It works
1. The files are moved to the new location
2. A [symbolic link](https://www.wikiwand.com/en/NTFS_junction_point) is created from the old location redirecting to the new one. Any program trying to access a file in the old location will automatically be redirected to its new location

## Core Features
- [x] In-app directory browsing/operations
- [x] Multi-language support
- [x] Move directories or files
- [x] Automatically create target directories
- [x] Create symbolic links
- [x] Quickly restore symbolic links
- [x] Everything search integration
- [x] Context menu integration
- [x] Individual file symbolic links
- [x] Size-ranked recommended moves with batch moving
- [x] Move history with undo, including individual folders from a batch
- [x] Batch progress (item i of j) with Skip and Skip remaining
- [x] In-use detection with End tasks and retry
- [x] Never overwrites silently: warns first, with Skip or Overwrite
- [x] Move queue: line up several manual moves (or drop several folders on the window) and move them in one go
- [x] Preview: size, free space before and after, programs using it and warnings, without moving anything
- [x] Warnings for risky moves: cloud-synced folders (detected from Windows, any sync app), Microsoft Store apps, Windows services, removable, network or non-NTFS drives
- [x] Health check: finds moved folders whose drive is missing or whose link was replaced by a reinstall, and recreates missing links (also runs at startup)
- [x] "Move everything back" before uninstalling FreeMove or wiping a drive
- [x] History backups, and "Find missing moves" to rebuild History from the links on disk
- [x] Space saved per drive shown in History
- [x] Log file for troubleshooting (Info > Open log folder)
- [x] Drag and drop folders onto the window

*Feel free to reach out with ideas for new implementations*

## Usage

>[!NOTE]
>
> This program requires administrator privileges for its core functionality

Run the executable and use the GUI

### Recommended Moves

Open **Recommended moves** from the menu bar to let FreeMove find what to move for you:

1. FreeMove scans the Windows drive (installed programs, AppData, Downloads/Videos/Music/Pictures, and folders you created at the drive root) and lists the folders above the minimum size, largest first.
2. Folders that are always in use are never suggested: Windows, driver, security and `Microsoft` folders, folders containing a service that starts with Windows, OneDrive folders, and folders that are already links. The **In use by** column shows programs running from a folder right now.
3. Pick a destination (defaults to `D:\FreeMove`) and tick the folders to move. Each one is placed at a mirrored path, e.g. `C:\Program Files\Foo` → `D:\FreeMove\Program Files\Foo`, and a link is left in its original place.
4. If a folder can't be moved, it is rolled back and the batch continues. A summary at the end shows what moved and how much space was freed.

### Folders in Use

Folders that a program is using are set aside instead of failing, and the rest of the batch carries on. At the end, FreeMove lists them with the programs using each one (found from running programs and open files) and offers:

- **End tasks and retry**: closes those programs (after confirming; unsaved work in them is lost) and tries again.
- **Retry**: try again after you close them yourself.
- **Skip these**: leave them where they are.

Services, system processes and Explorer are never ended. The same applies when undoing moves from History.

### Existing Files at the Destination

FreeMove never overwrites anything silently. If a folder with the same name already exists at the destination, you are asked first:

- For a batch, a list of the conflicts appears before anything moves. Unticked items are **skipped** (the default); ticked items are **overwritten**. Use **Overwrite all** or **Skip all** to set them all at once.
- For a single move, you are asked whether to overwrite or not move.

Overwriting merges into the existing folder: files with the same name are replaced by the ones being moved, and other files already there are kept. Each replaced file is kept in a hidden backup folder (`.FreeMoveReplaced` on that drive), and FreeMove records exactly which files the move added. If the move fails or is cancelled, the existing folder is put back exactly as it was.

Long messages (summaries, file lists, errors) open in a resizable, scrollable window so they always fit on screen.

### Batch Progress and Skipping

When several folders are queued (a Recommended moves batch, or undoing several items from History), the progress window shows **Item i of j** and the folder being processed, with two extra buttons:

- **Skip** stops the current folder and puts it back exactly as it was, then starts the next one.
- **Skip remaining** lets the current folder finish, then doesn't start the rest.

**Cancel** puts the current folder back and stops the whole batch. Skipped folders are listed in the summary at the end.

### History and Undo

Open **History** from the menu bar to see everything done with FreeMove, newest first: manual moves, Recommended moves batches, and undos.

- Every entry expands to show what it did: each folder, where it went, its size and its current state. Tick an entry to undo all of it, or expand it and tick only the folders you want back.
- Click **Undo selected** to move the ticked items back to their original location. The link is removed and the files are copied back from the other drive.
- Undos are recorded as their own entries, so you can see what was moved back and when.
- Moves that were already undone, or whose link or files were changed outside FreeMove, are greyed out and can't be ticked.
- **Clear finished** removes undone and no-longer-valid entries from the list.
- A merged move shows how much space its backups of replaced files take. Select it and click **Delete backups** to free that space. After that, those original files can't be restored, and undoing the move leaves the files that replaced them in place.

The history is stored in `%APPDATA%\FreeMove\History.xml`. Moves made before this version are not in it. Undoing a move that was merged into an existing folder moves back only the files that move added and restores any files it replaced; files that were already there stay where they were.

### How Moves Are Kept Safe

- **Verified before anything is deleted.** Files are copied to the new location and each copy is checked against the original. The original is deleted only after everything was copied. A different file that's already at the destination is never silently kept or overwritten.
- **Links inside folders are kept as links.** Symbolic links and junctions inside a moved folder are recreated, not followed, so FreeMove never copies what they point to or loops on them.
- **Faithful copies.** Attributes, timestamps, alternate data streams and the folder's own permissions are copied. Files hard-linked to each other stay hard-linked, and sparse files stay sparse.
- **Crash-safe.** Every move, undo and restore writes a record first (in `%APPDATA%\FreeMove\Journals`). If FreeMove or the PC stops halfway, the next start finishes the operation or rolls it back, and tells you what it did.
- **Nothing left half-done.** A move that fails is put back exactly as it was, including any folder created to hold it, and a move whose link can't be created is moved back.
- **Restore symbolic link** in the folder browser uses the same safe path as undoing from History.
- **Long paths.** Paths over 260 characters work. On the .NET 4.8 version, Windows' long path support must also be turned on (the "Enable Win32 long paths" policy).

### Command Line Arguments

| Argument | Description |
| :--- | :--- |
| `--unsafe-skip-checks` | Force disable all safety checks (not recommended) |
| `--source <source>` | Set the source directory to move from |
| `--destination <destination>` | Set the destination directory to move to |

## Recommendations

>[!WARNING]
>
> You should not move important system directories as they can break core functionalities like Windows Update and Windows Store Apps.

Important system directories such as:

- `C:\Users` 
- `C:\Documents and Settings`
- `C:\Program Files`
- `C:\Program Files (x86)` 

and so on should not be moved. If you wish to do It anyway do it at your own risk. To move a directory back refer to the FAQ part of the readme.

That said, moving directories **contained in** the previously mentioned directories should not cause any problem. So you are free to move `C:\Program Files\HugeProgramIDontWantOnMySSD` without any problem.

<details>
<summary>Safety Checks Range</summary>

#### Never moved, including everything inside them

- `C:\Windows`
- `C:\boot`, `C:\Recovery`, `C:\System Volume Information`, `C:\$Recycle.Bin`
- `C:\ProgramData\Microsoft`
- `C:\Program Files\WindowsApps`
- `%TEMP%`

#### Never moved themselves (moving something inside them is fine)

- `C:\`
- `C:\Users`, and every profile folder in it (`C:\Users\<name>`, `Public`, `Default`)
- `C:\ProgramData`
- `C:\Documents and Settings`

#### In safe mode

- `C:\Program Files\Common Files` and `C:\Program Files (x86)\Common Files`, including everything inside them
- `C:\Program Files`, `C:\Program Files (x86)`, `%APPDATA%`, `%LOCALAPPDATA%` themselves

#### Also refused

- Moving a folder into itself
- Moving something that is already a link

</details>

## Screenshots

<img src="https://i.imgur.com/xvkVdc6.png" width="400" alt="Screenshot" />

## FAQ

### Q: How to uninstall moved programs?

A: Uninstall the program just as you would normally without deleting the junction. The uninstaller will work normally leaving an empty directory in the location you moved the program to, and the directory link in the original location, both of which you can then delete manually.

### Q: How to move a program back?


#### Using the GUI

Select a symbolic link and right click it and select "Restore symbolic link".

#### Manual move

Delete the junction in the old location (this won't delete the content) and move the directory back to its original position.

### Q: How to create multiple symbolic links for the same file or folder?

A: Right-click the existing symbolic link in File Explorer and then select "Copy". Then paste it where you want like a normal file.

### Q: Everything not found or DLL missing

A: The Everything search integration requires [Everything](https://www.voidtools.com/downloads/) (Install or Portable) to be installed and `Everything64.dll` or `Everything32.dll` (depending on your system architecture) to be either in the same directory as the FreeMove executable or in the system environment variables.

### Q: Everything not running

A: The Everything search integration requires the Everything program to be running. Please make sure you have.

## Contributing

The [original project](https://github.com/imDema/FreeMove) is developed by [imDema](https://github.com/imDema). This repo is abandoned, and a new project was created by [fengyec2](https://github.com/fengyec2/FreeMove) containing additional features. I used this version as a base for my contributions.
