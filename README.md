# AIChef

Tell it what you have in the fridge and what meal you're cooking, and it suggests five
dishes; pick one and it writes the full recipe and generates a photo to go with it.

A hosted Blazor WebAssembly app backed by the OpenAI API.

Live: https://aichef-production-5349.up.railway.app/

## How it works

```
CreateMeal  ──▶  MealIdeas  ──▶  MealRecipe
 ingredients      5 dish ideas    recipe + generated photo
 + meal time
```

The three pages share state through `RecipeState`, a cascading value in
`Client/Shared/RecipeState.razor`, so the chosen ingredients survive navigation without a
round trip.

Recipe ideas and the recipe itself both come from a chat completion that is *forced* to
call a tool, using `tool_choice` with `strict: true`. The schema is declared in
`Server/Services/OpenAIService.cs`, which means the model has to answer with JSON matching
the `Idea` / `Recipe` shape rather than prose that then needs parsing.

## Project layout

| Project | What's in it |
| --- | --- |
| `Client` | Blazor WebAssembly UI — pages, layouts, `RecipeState` |
| `Server` | ASP.NET Core host, `RecipeController`, OpenAI integration, image cache |
| `Shared` | DTOs used by both sides (`Idea`, `Recipe`, `RecipeImage`, `RecipeParms`) |

Worth knowing where the seams are:

- `Server/ChatEndpoint/ChatEndpoint.cs` — hand-rolled OpenAI request/response models. No
  SDK dependency, so this file is what you edit when the API changes.
- `Server/Services/OpenAIService.cs` — builds the tool schemas and makes the three calls.
- `Server/Services/RecipeImageCache.cs` — see [Image caching](#image-caching) below.

## Running locally

Needs the .NET 10 SDK and an OpenAI API key.

```bash
# Set the key (stored outside the repo, not in appsettings.json)
dotnet user-secrets set "OpenAi:OpenAiKey" "sk-..." --project Server

dotnet run --project Server
```

Then open https://localhost:7198 (or http://localhost:5249).

To see the exact JSON being sent to OpenAI, which is the fastest way to debug a rejected
request:

```bash
Logging__LogLevel__AIChef=Debug dotnet run --project Server
```

## Configuration

All settings are optional except the key. Use `:` in `appsettings.json` / user secrets and
`__` for environment variables (`OpenAi__ChatModel`).

| Setting | Default | Notes |
| --- | --- | --- |
| `OpenAi:OpenAiKey` | *required* | Also read from `OpenAiKey` or `OPENAI_API_KEY` |
| `OpenAi:ChatModel` | `gpt-4o-mini` | Used for both recipe ideas and recipes |
| `OpenAi:ImageModel` | `gpt-image-1` | Must be a model your account can actually see — check `/v1/models` |
| `OpenAi:ImageQuality` | `low` | `low` / `medium` / `high`. The main cost lever |
| `OpenAi:ImageCachePath` | system temp dir | Where generated images are stored |

Leaving `ImageQuality` unset would let OpenAI default to `auto` and pick whatever it likes,
so it is set explicitly — at 1024×1024 the high tier costs about 15× the low tier.

## Image caching

Image generation costs orders of magnitude more than the text, so a generated image is
stored and reused rather than regenerated on every page view.

Images are cached to disk keyed on a SHA-256 of the trimmed, lowercased recipe title, and
served from `GET /Recipe/RecipeImage/{key}` with a long `immutable` cache header so
returning browsers don't even re-request them. Two consequences worth understanding:

- **Matching is exact, not fuzzy.** "Spicy Chickpea Curry" and "Chickpea Curry" are two
  different keys and two separate charges. Only an identical title reuses an image.
- **The bytes are stored, not the URL.** OpenAI's image URLs expire about an hour after
  they're issued, so caching the URL would produce broken images the next day.

The cache degrades to "always generate" if its directory can't be written, rather than
failing the request.

## API

| Endpoint | Purpose |
| --- | --- |
| `POST /Recipe/GetRecipeIdeas` | Five dish ideas from ingredients + meal time |
| `POST /Recipe/GetRecipe` | Full recipe for a chosen idea |
| `GET /Recipe/GetRecipeImage?title=` | Generates or returns a cached image |
| `GET /Recipe/RecipeImage/{key}` | Serves cached image bytes |

When image generation fails, `GetRecipeImage` returns an empty `data` array and the recipe
page renders without a picture. It deliberately does not substitute a placeholder photo —
a stand-in is indistinguishable from a real result and hides the failure.

## Deploying

The `Dockerfile` publishes the Server project, which serves the WebAssembly client as
static files. Railway builds it directly; set `OpenAi__OpenAiKey` as a service variable.

The Dockerfile sets `ASPNETCORE_HTTP_PORTS=80` to match its `EXPOSE 80`. This is load
bearing: .NET 8 changed the default container port to 8080, so without it the app listens
on 8080 while the host routes to 80 and every request fails with a 502.

The image cache lives in the container's temp directory, so it resets on each redeploy and
the first view of a given recipe pays again. Mount a volume and point
`OpenAi__ImageCachePath` at it to keep the cache across deploys.

## Known limitations

- **The image cache has no eviction.** It grows until the disk does. Fine on an ephemeral
  filesystem, worth revisiting with a persistent volume.
- **Concurrent first requests aren't deduplicated.** Two simultaneous requests for the
  same uncached title will both generate and both be billed.
