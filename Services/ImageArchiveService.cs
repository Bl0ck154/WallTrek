using System;
using System.IO;
using System.Threading.Tasks;

namespace WallTrek.Services
{
    public static class ImageArchiveService
    {
        private const string ArchiveFolderName = "archive";

        // Moves the file into (or out of) an "archive" subfolder next to it and updates the
        // database row, including video rows that reference it as their source image.
        // Returns the file's path after the move.
        public static async Task<string> SetArchivedAsync(string imagePath, bool archive)
        {
            var newPath = archive ? GetArchivePath(imagePath) : GetUnarchivePath(imagePath);

            if (newPath != imagePath && File.Exists(imagePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                newPath = MakeUnique(newPath);
                File.Move(imagePath, newPath);
            }
            else if (!File.Exists(imagePath))
            {
                newPath = imagePath; // nothing to move; just flag it in the database
            }

            var databaseService = new DatabaseService();
            await databaseService.SetImageArchivedAsync(imagePath, newPath, archive);
            return newPath;
        }

        private static string GetArchivePath(string imagePath)
        {
            var directory = Path.GetDirectoryName(imagePath)!;
            if (string.Equals(Path.GetFileName(directory), ArchiveFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return imagePath; // already sitting in an archive folder
            }
            return Path.Combine(directory, ArchiveFolderName, Path.GetFileName(imagePath));
        }

        private static string GetUnarchivePath(string imagePath)
        {
            var directory = Path.GetDirectoryName(imagePath)!;
            if (!string.Equals(Path.GetFileName(directory), ArchiveFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return imagePath; // not in an archive folder; just clear the flag
            }
            return Path.Combine(Path.GetDirectoryName(directory)!, Path.GetFileName(imagePath));
        }

        private static string MakeUnique(string path)
        {
            if (!File.Exists(path))
            {
                return path;
            }

            var directory = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                var candidate = Path.Combine(directory, $"{name} ({i}){extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
    }
}
