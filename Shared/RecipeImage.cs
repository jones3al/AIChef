using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace AIChef.Shared
{
    public class RecipeImage
    {
        public int Created { get; set; }
        public ImageData[]? Data { get; set; }

    }

    public class ImageData
    {
        public string? Url { get; set; }

        /// <summary>
        /// The image itself, base64 encoded, when the generation request asked for the
        /// "b64_json" response format. Preferred over Url for anything we intend to keep:
        /// the URLs OpenAI returns expire about an hour after they are issued.
        /// </summary>
        [JsonPropertyName("b64_json")]
        public string? B64Json { get; set; }
    }
}
