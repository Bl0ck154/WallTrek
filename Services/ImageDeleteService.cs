using System.IO;
using System.Threading.Tasks;

namespace WallTrek.Services
{
    public static class ImageDeleteService
    {
        // Removes the database row and the file on disk; callers handle exceptions.
        public static async Task DeleteAsync(string imagePath)
        {
            var databaseService = new DatabaseService();
            await databaseService.DeleteImageAsync(imagePath);

            if (File.Exists(imagePath))
            {
                File.Delete(imagePath);
            }
        }
    }
}
