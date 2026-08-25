using AiChef.Server.Data;
using AIChef.Client.Shared;
using AIChef.Server.Services;
using AIChef.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AIChef.Server.Controllers
{
    [Route("[controller]")]
    [ApiController]
    public class RecipeController : ControllerBase
    {

        private readonly IOpenAIAPI _openAIService;
        private readonly IRecipeImageCache _imageCache;

        public RecipeController(IOpenAIAPI openAIService, IRecipeImageCache imageCache)
        {
            _openAIService = openAIService;
            _imageCache = imageCache;
        }


        [HttpPost, Route("GetRecipeIdeas")]
        public async  Task<ActionResult<List<Idea>>> GetRecipeIdeas(RecipeParms recipeParms)
        {
            string? mealtime = recipeParms.MealTime;
            List<string> ingredients = recipeParms.Ingredients
                                                  .Where(x => !string.IsNullOrEmpty(x.Description))
                                                  .Select(x=> x.Description!)
                                                  .ToList();
            if (string.IsNullOrEmpty(mealtime) )
            {
                mealtime = "Breakfast";
            }
            
            var ideas = await _openAIService.CreateRecipeIdeas(mealtime, ingredients);

            return ideas;
            //return SampleData.RecipeIdeas;
        }

        [HttpPost, Route("GetRecipe")]
        public async Task<ActionResult<Recipe?>> GetRecipe(RecipeParms recipeParms)
        {
            List<string> ingredients = recipeParms.Ingredients
                                                  .Where(x => !string.IsNullOrEmpty(x.Description))
                                                  .Select(x=> x.Description!)
                                                  .ToList();

            string? title = recipeParms.SelectedIdea;

            if (string.IsNullOrEmpty(title) )
            {
                return BadRequest();
            }

            var recipe = await _openAIService.CreateRecipe(title, ingredients);
            return recipe;

            //return SampleData.Recipe;
        }

        [HttpGet, Route("GetRecipeImage")]
        public async Task<RecipeImage> GetRecipeImage(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return SampleData.RecipeImage;
            }

            // Serve an image we have already paid to generate rather than generating it
            // again. Image generation dwarfs the cost of everything else here, and
            // revisiting or refreshing a recipe would otherwise be billed each time.
            string? cachedUrl = _imageCache.TryGetUrl(title);

            if (cachedUrl is not null)
            {
                return AsRecipeImage(cachedUrl);
            }

            RecipeImage? generated = await _openAIService.CreateRecipeImage(title);
            string? base64Image = generated?.Data?.FirstOrDefault()?.B64Json;

            if (string.IsNullOrEmpty(base64Image))
            {
                return generated ?? SampleData.RecipeImage;
            }

            string? url = await _imageCache.SaveAsync(title, base64Image);

            // If it could not be cached the image is still perfectly good, so hand back
            // the URL OpenAI gave us if there was one rather than failing the request.
            return url is not null ? AsRecipeImage(url) : (generated ?? SampleData.RecipeImage);
        }

        /// <summary>
        /// Serves a cached image. Keys are content addressed, so an image at a given key
        /// never changes and the browser can hold onto it indefinitely.
        /// </summary>
        [HttpGet, Route("RecipeImage/{key}")]
        public async Task<IActionResult> RecipeImage(string key)
        {
            byte[]? image = await _imageCache.ReadAsync(key);

            if (image is null)
            {
                return NotFound();
            }

            Response.Headers.CacheControl = "public, max-age=31536000, immutable";

            return File(image, "image/png");
        }

        private static RecipeImage AsRecipeImage(string url) => new()
        {
            Data = new[] { new ImageData { Url = url } }
        };
    }
}
