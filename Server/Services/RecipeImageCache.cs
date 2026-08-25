using System.Security.Cryptography;
using System.Text;

namespace AIChef.Server.Services
{
    /// <summary>
    /// A file backed image cache keyed on the recipe title. Registered as a singleton so
    /// every request shares the same directory.
    /// </summary>
    public class RecipeImageCache : IRecipeImageCache
    {
        // Keys are hex encoded SHA-256 hashes, so anything else is either a bug or an
        // attempt at path traversal and must never reach the filesystem.
        private const int KeyLength = 64;

        private readonly ILogger<RecipeImageCache> _logger;
        private readonly string _directory;
        private readonly bool _enabled;

        public RecipeImageCache(IConfiguration configuration, ILogger<RecipeImageCache> logger)
        {
            _logger = logger;

            // Defaults to the temp directory because it is reliably writable in a
            // container. Point OpenAi:ImageCachePath at a mounted volume to keep the
            // cache across deploys.
            _directory = configuration["OpenAi:ImageCachePath"]
                         ?? Path.Combine(Path.GetTempPath(), "aichef-recipe-images");

            try
            {
                Directory.CreateDirectory(_directory);
                _enabled = true;
                _logger.LogInformation("Caching recipe images in {Directory}", _directory);
            }
            catch (Exception ex)
            {
                // A cache we cannot write to should degrade to "always generate", not
                // take the image endpoint down with it.
                _enabled = false;
                _logger.LogError(ex, "Could not create the recipe image cache directory {Directory}. Images will not be cached.", _directory);
            }
        }

        public string? TryGetUrl(string title)
        {
            if (!_enabled || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            string key = GetKey(title);

            return File.Exists(GetPath(key)) ? GetUrl(key) : null;
        }

        public async Task<string?> SaveAsync(string title, string base64Image)
        {
            if (!_enabled || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(base64Image))
            {
                return null;
            }

            string key = GetKey(title);

            try
            {
                byte[] bytes = Convert.FromBase64String(base64Image);

                // Write to a temporary name first and move it into place, so a failure
                // partway through cannot leave a truncated image that later reads would
                // treat as a valid cache hit.
                string path = GetPath(key);
                string pending = $"{path}.{Guid.NewGuid():N}.tmp";

                await File.WriteAllBytesAsync(pending, bytes);
                File.Move(pending, path, overwrite: true);

                return GetUrl(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not cache the generated image for {Title}", title);
                return null;
            }
        }

        public async Task<byte[]?> ReadAsync(string key)
        {
            if (!_enabled || !IsValidKey(key))
            {
                return null;
            }

            string path = GetPath(key);

            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return await File.ReadAllBytesAsync(path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not read the cached image {Key}", key);
                return null;
            }
        }

        /// <summary>
        /// Hashes the title so that the key is a fixed length, filesystem safe string.
        /// Trimmed and lowercased first so trivial differences in the title still hit.
        /// </summary>
        private static string GetKey(string title)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(title.Trim().ToLowerInvariant()));

            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static bool IsValidKey(string? key) =>
            key is { Length: KeyLength } && key.All(Uri.IsHexDigit);

        private string GetPath(string key) => Path.Combine(_directory, $"{key}.png");

        private static string GetUrl(string key) => $"/Recipe/RecipeImage/{key}";
    }
}
