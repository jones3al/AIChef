namespace AIChef.Server.Services
{
    /// <summary>
    /// Stores generated recipe images so that viewing the same recipe twice does not pay
    /// to generate it twice. Images are held as bytes rather than as the URL OpenAI
    /// returns, because that URL expires roughly an hour after it is issued.
    /// </summary>
    public interface IRecipeImageCache
    {
        /// <summary>
        /// The relative URL of the already-cached image for this title, or null if there
        /// isn't one and it needs generating.
        /// </summary>
        string? TryGetUrl(string title);

        /// <summary>
        /// Stores a base64 encoded image against a title and returns the relative URL it
        /// can be served from, or null if it could not be decoded or written.
        /// </summary>
        Task<string?> SaveAsync(string title, string base64Image);

        /// <summary>
        /// The bytes of a cached image, or null if the key is unknown or malformed.
        /// </summary>
        Task<byte[]?> ReadAsync(string key);
    }
}
