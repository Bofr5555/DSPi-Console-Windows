/*
 * DSPi Console launcher.
 *
 * The release folder keeps a single executable at its root; everything the app
 * actually needs lives in "app\". That split exists because this is an
 * unpackaged WinUI 3 app: the WindowsAppSDK native DLLs are loaded by the OS
 * from the executable's own directory, so the real DSPiConsole.exe cannot be
 * separated from them. Moving the whole payload into a subfolder and starting
 * it from here keeps that requirement intact.
 *
 * It also installs updates. The app downloads, verifies and unpacks a release
 * into "app.pending\" (and "DSPiConsole.exe.pending" when the launcher itself
 * changed), then exits and starts this launcher with
 *
 *     --install-update <pid> [--no-launch]
 *
 * The launcher waits for that process to end, renames "app\" aside and
 * "app.pending\" into its place, and starts the new app. An ordinary start
 * installs a staged update the same way. Nothing is ever replaced while the app
 * runs: a swap that cannot start (files in use) changes nothing and leaves the
 * update staged for the next start, and one that fails halfway is rolled back.
 * See DSPiConsole.Core/Updates/UpdateLayout.cs for the app's side.
 *
 * The launcher exits as soon as the app is started; it does not stay resident.
 * Built with MinGW: see build.ps1.
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>

#define APP_SUBDIR      L"\\app"
#define PENDING_SUBDIR  L"\\app.pending"
#define OLD_SUBDIR      L"\\app.old"
#define WORK_SUBDIR     L"\\app.update"
#define APP_EXE_NAME    L"\\DSPiConsole.exe"
#define PENDING_STUB    L"\\DSPiConsole.exe.pending"
#define OLD_STUB        L"\\DSPiConsole.exe.old"

#define ARG_INSTALL     L"--install-update"
#define ARG_NO_LAUNCH   L"--no-launch"
#define ARG_NO_UPDATE   L" --no-update"

#define PATH_CHARS 1024

enum swap_result { SWAP_NONE, SWAP_DONE, SWAP_BUSY, SWAP_FAILED };

static void fail(const wchar_t *msg)
{
    MessageBoxW(NULL, msg, L"DSPi Console", MB_ICONERROR | MB_OK);
}

static BOOL exists(const wchar_t *path)
{
    return GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;
}

/* dst = a + b, refusing anything that would not fit. */
static BOOL join(wchar_t *dst, const wchar_t *a, const wchar_t *b)
{
    if (lstrlenW(a) + lstrlenW(b) >= PATH_CHARS) return FALSE;
    lstrcpyW(dst, a);
    lstrcatW(dst, b);
    return TRUE;
}

/* Deletes a folder and everything in it, best effort. Links are removed, never
 * followed. */
static void delete_tree(const wchar_t *path)
{
    wchar_t pattern[PATH_CHARS];
    if (!join(pattern, path, L"\\*")) return;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW(pattern, &fd);
    if (h != INVALID_HANDLE_VALUE)
    {
        do
        {
            if (lstrcmpW(fd.cFileName, L".") == 0 || lstrcmpW(fd.cFileName, L"..") == 0) continue;
            wchar_t child[PATH_CHARS];
            if (lstrlenW(path) + 1 + lstrlenW(fd.cFileName) >= PATH_CHARS) continue;
            lstrcpyW(child, path);
            lstrcatW(child, L"\\");
            lstrcatW(child, fd.cFileName);
            SetFileAttributesW(child, FILE_ATTRIBUTE_NORMAL);
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            {
                if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT)) delete_tree(child);
                else RemoveDirectoryW(child);
            }
            else
            {
                DeleteFileW(child);
            }
        } while (FindNextFileW(h, &fd));
        FindClose(h);
    }
    SetFileAttributesW(path, FILE_ATTRIBUTE_NORMAL);
    RemoveDirectoryW(path);
}

/* Renames, retrying for a few seconds while something (a virus scanner, the
 * exiting app's last handles) still holds a file in the folder. */
static BOOL move_retrying(const wchar_t *from, const wchar_t *to, DWORD flags)
{
    for (int attempt = 0; attempt < 25; attempt++)
    {
        if (MoveFileExW(from, to, flags)) return TRUE;
        DWORD err = GetLastError();
        if (err != ERROR_ACCESS_DENIED && err != ERROR_SHARING_VIOLATION && err != ERROR_LOCK_VIOLATION)
            return FALSE;
        Sleep(200);
    }
    return FALSE;
}

/* The major, minor and patch numbers of an executable's file version, packed
 * for comparison; 0 when it has none. */
static ULONGLONG file_version(const wchar_t *path)
{
    DWORD ignored = 0;
    DWORD size = GetFileVersionInfoSizeW(path, &ignored);
    if (size == 0) return 0;
    void *data = HeapAlloc(GetProcessHeap(), 0, size);
    if (data == NULL) return 0;
    ULONGLONG version = 0;
    VS_FIXEDFILEINFO *info = NULL;
    UINT len = 0;
    if (GetFileVersionInfoW(path, 0, size, data)
        && VerQueryValueW(data, L"\\", (void **)&info, &len) && info != NULL && len >= sizeof(*info))
    {
        version = ((ULONGLONG)HIWORD(info->dwFileVersionMS) << 32)
                | ((ULONGLONG)LOWORD(info->dwFileVersionMS) << 16)
                | HIWORD(info->dwFileVersionLS);
    }
    HeapFree(GetProcessHeap(), 0, data);
    return version;
}

/* Deletes every "app.old-<n>" folder an earlier start had to set aside. */
static void delete_set_aside(const wchar_t *dir, const wchar_t *old)
{
    wchar_t pattern[PATH_CHARS];
    if (!join(pattern, old, L"-*")) return;
    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirstFileW(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do
    {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        wchar_t child[PATH_CHARS];
        if (lstrlenW(dir) + 1 + lstrlenW(fd.cFileName) >= PATH_CHARS) continue;
        lstrcpyW(child, dir);
        lstrcatW(child, L"\\");
        lstrcatW(child, fd.cFileName);
        delete_tree(child);
    } while (FindNextFileW(h, &fd));
    FindClose(h);
}

/* Drops a staged update: its executable first, so a delete that stops partway
 * cannot leave something the next start would try to install. */
static void discard_pending(const wchar_t *pending, const wchar_t *pendingExe, const wchar_t *pendingStub)
{
    DeleteFileW(pendingExe);
    delete_tree(pending);
    DeleteFileW(pendingStub);
}

/* Swaps a staged update into place, after putting right a swap that an
 * earlier start did not finish. */
static enum swap_result install_pending(const wchar_t *dir)
{
    wchar_t app[PATH_CHARS], appExe[PATH_CHARS], pending[PATH_CHARS], pendingExe[PATH_CHARS];
    wchar_t old[PATH_CHARS], oldExe[PATH_CHARS];
    wchar_t stub[PATH_CHARS], pendingStub[PATH_CHARS], oldStub[PATH_CHARS], work[PATH_CHARS];
    if (!join(app, dir, APP_SUBDIR) || !join(appExe, app, APP_EXE_NAME)
        || !join(pending, dir, PENDING_SUBDIR) || !join(pendingExe, pending, APP_EXE_NAME)
        || !join(old, dir, OLD_SUBDIR) || !join(oldExe, old, APP_EXE_NAME)
        || !join(stub, dir, APP_EXE_NAME) || !join(pendingStub, dir, PENDING_STUB)
        || !join(oldStub, dir, OLD_STUB) || !join(work, dir, WORK_SUBDIR))
        return SWAP_NONE;

    /* A swap stopped between its two renames (the launcher was killed, or a
     * rollback failed): "app\" is missing. The previous version goes back,
     * and a staged update is then installed as usual. */
    if (!exists(app))
    {
        if (exists(oldExe)) MoveFileExW(old, app, 0);
        else if (exists(pendingExe)) MoveFileExW(pending, app, 0);
    }

    /* A previous install's leftovers, if its clean-up was interrupted. Only
     * ever once "app\" is in place, so the one working copy is never lost. */
    if (exists(appExe))
    {
        if (exists(old)) delete_tree(old);
        delete_set_aside(dir, old);
    }

    if (!exists(pendingExe) || !exists(appExe)) return SWAP_NONE;

    /* Never install an older version than the one in place (a stale update
     * left staged while a newer release was unpacked over the folder). */
    ULONGLONG current = file_version(appExe), staged = file_version(pendingExe);
    if (current != 0 && staged != 0 && staged < current)
    {
        discard_pending(pending, pendingExe, pendingStub);
        return SWAP_NONE;
    }

    if (exists(old))
    {
        wchar_t aside[PATH_CHARS];
        wsprintfW(aside, L"%s-%lu", old, GetTickCount());
        if (!MoveFileExW(old, aside, 0)) return SWAP_BUSY;
    }

    /* Nothing has changed until this succeeds, so failing here keeps the
     * update for the next start. */
    if (!move_retrying(app, old, 0)) return SWAP_BUSY;
    if (!move_retrying(pending, app, 0))
    {
        /* A rollback that fails too keeps everything: the next start puts
         * the previous version back (above) and tries again. */
        if (!move_retrying(old, app, 0)) return SWAP_BUSY;
        discard_pending(pending, pendingExe, pendingStub);
        return SWAP_FAILED;
    }

    /* A new launcher, when the release changed it. A running executable can be
     * renamed though not deleted, so this one steps aside and the next start
     * removes it. The old launcher still works, so a failure here is harmless. */
    if (exists(pendingStub))
    {
        DeleteFileW(oldStub);
        if (MoveFileExW(stub, oldStub, MOVEFILE_REPLACE_EXISTING))
        {
            if (!MoveFileExW(pendingStub, stub, 0)) MoveFileExW(oldStub, stub, 0);
        }
        DeleteFileW(pendingStub);
    }

    delete_tree(old);
    delete_tree(work);
    return SWAP_DONE;
}

/* Reads "--install-update <pid> [--no-launch]". */
static BOOL parse_install(const wchar_t *cmd, DWORD *pid, BOOL *noLaunch)
{
    while (*cmd == L' ') cmd++;
    int n = lstrlenW(ARG_INSTALL);
    if (wcsncmp(cmd, ARG_INSTALL, n) != 0 || (cmd[n] != L' ' && cmd[n] != L'\0')) return FALSE;
    *pid = wcstoul(cmd + n, NULL, 10);
    *noLaunch = wcsstr(cmd + n, ARG_NO_LAUNCH) != NULL;
    return TRUE;
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE hPrev, PWSTR lpCmdLine, int nShow)
{
    (void)hInst;
    (void)hPrev;

    /* Directory containing this launcher. */
    wchar_t dir[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, dir, MAX_PATH);
    if (n == 0 || n >= MAX_PATH)
    {
        fail(L"DSPi Console could not determine where it was started from.");
        return 1;
    }
    wchar_t *slash = wcsrchr(dir, L'\\');
    if (slash == NULL)
    {
        fail(L"DSPi Console could not determine where it was started from.");
        return 1;
    }
    *slash = L'\0';

    DWORD waitPid = 0;
    BOOL noLaunch = FALSE;
    BOOL installing = lpCmdLine != NULL && parse_install(lpCmdLine, &waitPid, &noLaunch);

    /* The app hands over just before it exits; wait until it has. */
    if (installing && waitPid != 0)
    {
        HANDLE proc = OpenProcess(SYNCHRONIZE, FALSE, waitPid);
        if (proc != NULL)
        {
            WaitForSingleObject(proc, 60000);
            CloseHandle(proc);
        }
    }

    /* Last install's launcher, which could not delete itself while running. */
    wchar_t oldStub[PATH_CHARS];
    if (join(oldStub, dir, OLD_STUB)) DeleteFileW(oldStub);

    enum swap_result swap = install_pending(dir);
    if (swap == SWAP_FAILED)
    {
        fail(L"The DSPi Console update could not be installed, so the current version was kept.\n\n"
             L"Check for updates again from Help, or download the release from GitHub.");
    }
    else if (swap == SWAP_BUSY && installing)
    {
        /* Most likely another DSPi Console is still open. The app still
         * starts when it was asked to, marked so it doesn't hand the update
         * straight back. */
        MessageBoxW(NULL,
            L"The DSPi Console update could not be installed yet, because its files are in use. "
            L"It will be installed the next time DSPi Console starts with no other copy open.",
            L"DSPi Console", MB_ICONINFORMATION | MB_OK);
    }
    if (noLaunch) return 0;

    wchar_t appDir[PATH_CHARS];
    wchar_t appExe[PATH_CHARS];
    if (!join(appDir, dir, APP_SUBDIR) || !join(appExe, appDir, APP_EXE_NAME))
    {
        fail(L"DSPi Console could not determine where it was started from.");
        return 1;
    }

    if (!exists(appExe))
    {
        fail(L"DSPi Console could not find its program files.\n\n"
             L"Expected to find app\\DSPiConsole.exe next to this launcher. "
             L"Extract the whole release folder and keep its structure intact, "
             L"then run DSPi Console again.");
        return 1;
    }

    /*
     * Command line for the child: the quoted target, then anything we were
     * given (not the install arguments, which were for us). An update still
     * staged is marked so the app does not hand it straight back. CreateProcessW
     * may write to this buffer, so it must be writable. Arguments are dropped
     * rather than truncated if they would not fit, since a half-copied command
     * line is worse than none.
     */
    static wchar_t cmdline[32768];
    const int cap = (int)(sizeof(cmdline) / sizeof(cmdline[0]));
    const int exeLen = lstrlenW(appExe);
    const int argLen = (!installing && lpCmdLine != NULL) ? lstrlenW(lpCmdLine) : 0;

    cmdline[0] = L'"';
    lstrcpyW(cmdline + 1, appExe);
    lstrcatW(cmdline, L"\"");
    if (argLen > 0 && (exeLen + argLen + 4) < cap)
    {
        lstrcatW(cmdline, L" ");
        lstrcatW(cmdline, lpCmdLine);
    }
    if (swap == SWAP_BUSY && lstrlenW(cmdline) + lstrlenW(ARG_NO_UPDATE) < cap)
        lstrcatW(cmdline, ARG_NO_UPDATE);

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = (WORD)nShow;
    ZeroMemory(&pi, sizeof(pi));

    /* Working directory is the app folder so relative paths resolve as they
     * did when the executable sat in the release root. */
    if (!CreateProcessW(appExe, cmdline, NULL, NULL, FALSE, 0, NULL, appDir, &si, &pi))
    {
        fail(L"DSPi Console could not be started.\n\n"
             L"app\\DSPiConsole.exe is present but would not launch. It may be "
             L"blocked by security software, or the download may be incomplete.");
        return 1;
    }

    /* Let the app take the foreground, then get out of the way. */
    AllowSetForegroundWindow(pi.dwProcessId);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}
