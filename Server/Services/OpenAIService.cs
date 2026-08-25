using AIChef.Server.ChatEndpoint;
using AIChef.Shared;
using System.Linq.Expressions;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIChef.Server.Services
{
    public class OpenAIService : IOpenAIAPI
    {
        private readonly IConfiguration _configuration;
        private static readonly string _baseUrl = "https://api.openai.com/v1/";
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly JsonSerializerOptions _jsonOptions;

        // Build the tool schema so the AI returns a JSON formatted object.
        // Note: the names in every Required array must be the *serialized* (camelCase)
        // property names, since _jsonOptions renames properties on the way out.
        private static ChatFunction.Parameter _recipeIdeaParameter = new()
        {
            // describes one Idea
            Type = "object",
            Required = new string[] { "index", "title", "description" },
            AdditionalProperties = false,
            Properties = new
            {
                // provide a type and description for each property of the Idea model
                Index = new ChatFunction.Property
                {
                    Type = "integer",
                    Description = "A unique identifier for this object",
                },
                Title = new ChatFunction.Property
                {
                    Type = "string",
                    Description = "The name for a recipe to create"
                },
                Description = new ChatFunction.Property
                {
                    Type = "string",
                    Description = "A description of the recipe"
                }
            }
        };

        private static ChatFunction _ideaFunction = new()
        {
            // describe the function we want an argument for from the AI
            Name = "CreateRecipe",
            // this description ensures we get 5 ideas back
            Description = "Generates recipes for each idea in an array of five recipe ideas",
            Strict = true,
            Parameters = new
            {
                // OpenAI requires that the parameters are an object, so we need to wrap our array in an object
                Type = "object",
                Properties = new
                {
                    Data = new // our array will come back in an object in the Data property
                    {
                        Type = "array",
                        // further ensures the AI will create 5 recipe ideas
                        Description = "An array of five recipe ideas",
                        Items = _recipeIdeaParameter
                    }
                },
                Required = new[] { "data" },
                AdditionalProperties = false
            }
        };

        private static ChatFunction.Parameter _recipeParameter = new()
        {
            Type = "object",
            Description = "The recipe to display",
            Required = new[] { "title", "ingredients", "instructions", "summary" },
            AdditionalProperties = false,
            Properties = new
            {
                Title = new
                {
                    Type = "string",
                    Description = "The title of the recipe to display",
                },
                Ingredients = new
                {
                    Type = "array",
                    Description = "An array of all the ingredients mentioned in the recipe instructions",
                    Items = new { Type = "string" }
                },
                Instructions = new
                {
                    Type = "array",
                    Description = "An array of each step for cooking this recipe",
                    Items = new { Type = "string" }
                },
                Summary = new
                {
                    Type = "string",
                    Description = "A summary description of what this recipe creates",
                },
            },
        };

        private static ChatFunction _recipeFunction = new()
        {
            Name = "DisplayRecipe",
            Description = "Displays the recipe from the parameter to the user",
            Strict = true,
            Parameters = new
            {
                Type = "object",
                Properties = new
                {
                    Data = _recipeParameter
                },
                Required = new[] { "data" },
                AdditionalProperties = false
            }
        };

        // Wraps a function definition as a tool, and builds the tool_choice value that
        // forces the model to call it.
        private static ChatTool[] AsTools(ChatFunction function) =>
            new[] { new ChatTool { Function = function } };

        private static object ForceToolChoice(ChatFunction function) =>
            new { Type = "function", Function = new { Name = function.Name } };

        // The model's chosen tool call arguments, or null if it did not call the tool.
        private static string? GetToolCallArguments(ChatResponse? response) =>
            response?.Choices?
                    .FirstOrDefault(c => c.Message?.ToolCalls?.Length > 0)?
                    .Message?
                    .ToolCalls?
                    .FirstOrDefault()?
                    .Function?
                    .Arguments;

        private readonly ILogger<OpenAIService> _logger;
        private readonly string? _apiKey;
        private readonly string _chatModel;

        public OpenAIService(IConfiguration configuration, ILogger<OpenAIService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            // Overridable via config so the model can be changed without a code change.
            _chatModel = _configuration["OpenAi:ChatModel"] ?? "gpt-4o-mini";

            // Accept the conventional OPENAI_API_KEY name too, so the key is found
            // regardless of which convention the host (Railway, Docker, etc.) was configured with.
            _apiKey = _configuration["OpenAi:OpenAiKey"]
                      ?? _configuration["OpenAiKey"]
                      ?? Environment.GetEnvironmentVariable("OpenAiKey")
                      ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                _logger.LogError("No OpenAI API key found. Set one of: OpenAi__OpenAiKey, OpenAiKey, or OPENAI_API_KEY.");
            }

            _jsonOptions = new()
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        /// <summary>
        /// Posts to OpenAI with per-request headers. The HttpClient is static, so its
        /// DefaultRequestHeaders must not be mutated here - this service is scoped, and
        /// doing so would append a duplicate Accept header on every single request until
        /// the header grew large enough for the request to be rejected outright.
        /// </summary>
        private async Task<HttpResponseMessage> PostToOpenAI<TRequest>(string url, TRequest body)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body, options: _jsonOptions)
            };

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            // The API key travels in the header, not the body, so the body is safe to log.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("OpenAI request to {Url}: {Body}",
                                 url, JsonSerializer.Serialize(body, _jsonOptions));
            }

            return await _httpClient.SendAsync(request);
        }

        /// <summary>
        /// Logs the response body when OpenAI returns a non-success status. Without this the
        /// error JSON gets deserialized into an empty model and the failure looks like a
        /// successful-but-empty result to the caller.
        /// </summary>
        private async Task<bool> IsSuccess(HttpResponseMessage httpResponse, string operation)
        {
            if (httpResponse.IsSuccessStatusCode)
            {
                return true;
            }

            string body = await httpResponse.Content.ReadAsStringAsync();
            _logger.LogError("OpenAI {Operation} failed with {StatusCode}: {Body}",
                             operation, (int)httpResponse.StatusCode, body);
            return false;
        }

        public async Task<List<Idea>> CreateRecipeIdeas(string mealtime, List<string> ingredientList)
        {
            string url = $"{_baseUrl}chat/completions";
            string systemPrompt = "You are a world-renowned chef. I will send you a list of ingredients and a meal time. You will respond with 5 ideas for dishes.";
            string userPrompt = "";
            string ingredientPrompt = "";

            string ingredients = string.Join(",", ingredientList);

            if (string.IsNullOrEmpty(ingredients))
            {
                ingredientPrompt = "Suggest some ingredients for me";
            }
            else
            {
                ingredientPrompt = $"I have {ingredients}";
            }

            userPrompt = $"The meal I want to cook is {mealtime}. {ingredientPrompt}";
            ChatMessage systemMessage = new ChatMessage()
            {
                Role = "system",
                Content = $"{systemPrompt}"
            };

            ChatMessage userMessage = new ChatMessage()
            {
                Role = "user",
                Content = $"{userPrompt}"
            };

            ChatRequest request = new ChatRequest()
            {
                Model = _chatModel,
                Messages = new[] { systemMessage, userMessage },
                Tools = AsTools(_ideaFunction),
                ToolChoice = ForceToolChoice(_ideaFunction)
            };

            //make call to open ai
            using HttpResponseMessage httpResponse = await PostToOpenAI(url, request);

            if (!await IsSuccess(httpResponse, nameof(CreateRecipeIdeas)))
            {
                return new List<Idea>();
            }

            //get response
            ChatResponse? response = await httpResponse.Content.ReadFromJsonAsync<ChatResponse>();

            //get the arguments of the tool the model chose to call
            string? arguments = GetToolCallArguments(response);

            if (arguments is null)
            {
                _logger.LogError("OpenAI {Operation} returned no tool call.", nameof(CreateRecipeIdeas));
                return new List<Idea>();
            }

            try
            {
                Result<List<Idea>>? ideaResult =
                    JsonSerializer.Deserialize<Result<List<Idea>>>(arguments, _jsonOptions);

                return ideaResult?.Data ?? new List<Idea>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not deserialize recipe ideas from tool arguments: {Arguments}", arguments);
                return new List<Idea>();
            }
        }

        public async Task<Recipe?> CreateRecipe(string title, List<string> ingredients)
        {
            string url = $"{_baseUrl}chat/completions";
            string systemPrompt = "You are a world-renowned chef. Create the recipe with ingredients, instructions, and a summary";
            string userPrompt = $"Create a {title} recipe.";

            ChatMessage userMessage = new ChatMessage()
            {
                Role = "user",
                Content = $"{systemPrompt} {userPrompt}"
            };

            ChatRequest request = new ChatRequest()
            {
                Model = _chatModel,
                Messages = new[] { userMessage },
                Tools = AsTools(_recipeFunction),
                ToolChoice = ForceToolChoice(_recipeFunction)
            };

            using HttpResponseMessage httpResponse = await PostToOpenAI(url, request);

            if (!await IsSuccess(httpResponse, nameof(CreateRecipe)))
            {
                return null;
            }

            ChatResponse? response = await httpResponse.Content.ReadFromJsonAsync<ChatResponse?>();

            string? arguments = GetToolCallArguments(response);

            if (arguments is null)
            {
                _logger.LogError("OpenAI {Operation} returned no tool call.", nameof(CreateRecipe));
                return null;
            }

            try
            {
                Result<Recipe>? recipe = JsonSerializer.Deserialize<Result<Recipe>>(arguments, _jsonOptions);
                return recipe?.Data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not deserialize recipe from tool arguments: {Arguments}", arguments);
                return null;
            }
        }

        public async Task<RecipeImage?> CreateRecipeImage(string recipeTitle)
        {
            string url = $"{_baseUrl}images/generations";
            string userPrompt = $"Create a restaurant product shot for {recipeTitle}";

            ImageGenerationRequest request = new()
            {
                Prompt = userPrompt,
                // Ask for the bytes rather than a URL. The URL OpenAI returns expires
                // within the hour, so it cannot be cached, and re-generating an image is
                // by far the most expensive thing this app does.
                ResponseFormat = "b64_json"
            };

            using HttpResponseMessage httpResponse = await PostToOpenAI(url, request);

            if (!await IsSuccess(httpResponse, nameof(CreateRecipeImage)))
            {
                return null;
            }

            RecipeImage? recipeImage = null;

            try
            {
                recipeImage = await httpResponse.Content.ReadFromJsonAsync<RecipeImage>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recipe image response could not be deserialized.");
                return null;
            }

            // An error payload deserializes into a RecipeImage with no Data, which is not
            // null and so would defeat the caller's fallback. Treat it as a failure.
            if (recipeImage?.Data is null || recipeImage.Data.Length == 0)
            {
                _logger.LogError("Recipe image response contained no image data.");
                return null;
            }

            return recipeImage;

        }
    }
}
