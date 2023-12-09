using AIChef.Shared;

namespace AIChef.Server.Services
{
    public interface IOpenAIAPI
    {
        Task<List<Idea>> CreateRecipeIdeas(string mealtime, List<string> ingredients);
        Task<Recipe?> CreateRecipe(string title, List<string> ingredients);

        Task<RecipeImage?> CreateRecipeImage(string recipeTitle);
    }
}
