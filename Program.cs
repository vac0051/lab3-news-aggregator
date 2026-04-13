using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Lab3.NewsAggregator;

internal static partial class Program
{
    private static async Task Main()
    {
        Console.WriteLine("ЛР3: Асинхронный агрегатор данных");
        Console.WriteLine("Нажмите C для отмены операции...\n");

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        _ = Task.Run(() => ListenForCancellation(cts));

        var aggregator = new DataAggregator(httpClient);

        try
        {
            var dashboard = await aggregator.LoadDashboardAsync(cts.Token);

            Console.WriteLine("=== Результаты асинхронных запросов ===");
            Console.WriteLine($"Погода (Open-Meteo API): {dashboard.WeatherSummary}");
            Console.WriteLine($"Курсы (Frankfurter + ЦБ РФ API): {dashboard.RatesSummary}");

            Console.WriteLine("\nНовости (Hacker News API):");
            foreach (var headline in dashboard.HackerNewsHeadlines)
            {
                Console.WriteLine($"- {headline}");
            }

            Console.WriteLine("\nНовости (Spaceflight News API):");
            foreach (var headline in dashboard.SpaceflightHeadlines)
            {
                Console.WriteLine($"- {headline}");
            }

            Console.WriteLine("\n=== Параллельный анализ текста ===");
            var combinedHeadlines = dashboard.HackerNewsHeadlines
                .Concat(dashboard.SpaceflightHeadlines)
                .ToList();

            var topWords = TextAnalyzer.FindTopWordsParallel(combinedHeadlines, 8, cts.Token);
            foreach (var word in topWords)
            {
                Console.WriteLine($"{word.Key}: {word.Value}");
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Операция была отменена пользователем или по таймауту.");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Ошибка: {exception.Message}");
        }
    }

    private static void ListenForCancellation(CancellationTokenSource cts)
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        while (!cts.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(150);
                continue;
            }

            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.C)
            {
                cts.Cancel();
                return;
            }
        }
    }
}

internal sealed class DataAggregator
{
    private readonly HttpClient _httpClient;

    public DataAggregator(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<DashboardResult> LoadDashboardAsync(CancellationToken cancellationToken)
    {
        var weatherTask = GetWeatherSummarySafeAsync(cancellationToken);
        var ratesTask = GetRatesSummarySafeAsync(cancellationToken);
        var hackerNewsTask = GetHackerNewsHeadlinesSafeAsync(cancellationToken);
        var spaceflightTask = GetSpaceflightHeadlinesSafeAsync(cancellationToken);

        await Task.WhenAll(weatherTask, ratesTask, hackerNewsTask, spaceflightTask);

        return new DashboardResult(
            weatherTask.Result,
            ratesTask.Result,
            hackerNewsTask.Result,
            spaceflightTask.Result);
    }

    private async Task<string> GetWeatherSummarySafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetWeatherSummaryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return "данные погоды недоступны (offline fallback)";
        }
    }

    private async Task<string> GetRatesSummarySafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetRatesSummaryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return "данные о курсах недоступны (offline fallback)";
        }
    }

    private async Task<IReadOnlyList<string>> GetHackerNewsHeadlinesSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetHackerNewsHeadlinesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return
            [
                "offline fallback: hacker news headline one",
                "offline fallback: hacker news headline two"
            ];
        }
    }

    private async Task<IReadOnlyList<string>> GetSpaceflightHeadlinesSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetSpaceflightHeadlinesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return
            [
                "offline fallback: spaceflight headline one",
                "offline fallback: spaceflight headline two"
            ];
        }
    }

    private async Task<string> GetWeatherSummaryAsync(CancellationToken cancellationToken)
    {
        const string url = "https://api.open-meteo.com/v1/forecast?latitude=55.75&longitude=37.62&current=temperature_2m,wind_speed_10m";
        var weatherResponse = await _httpClient.GetFromJsonAsync<WeatherApiResponse>(url, cancellationToken);

        if (weatherResponse?.Current is null)
        {
            return "нет данных";
        }

        return $"Москва {weatherResponse.Current.Temperature2m:F1}°C, ветер {weatherResponse.Current.WindSpeed10m:F1} м/с";
    }

            private async Task<string> GetRatesSummaryAsync(CancellationToken cancellationToken)
    {
        const string eurUrl = "https://api.frankfurter.app/latest?from=USD&to=EUR";
        const string rubUrl = "https://www.cbr-xml-daily.ru/daily_json.js";

        var eurText = "n/a";
        var rubText = "n/a";

        try
        {
            var eurResponse = await _httpClient.GetFromJsonAsync<RatesApiResponse>(eurUrl, cancellationToken);
            if (eurResponse?.Rates is not null && eurResponse.Rates.TryGetValue("EUR", out var eur))
            {
                eurText = eur.ToString("F3");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        try
        {
            var rubResponse = await _httpClient.GetFromJsonAsync<CbrDailyResponse>(rubUrl, cancellationToken);
            if (rubResponse?.Valute?.Usd is not null)
            {
                rubText = rubResponse.Valute.Usd.Value.ToString("F3");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }

        return $"1 USD = {eurText} EUR, {rubText} RUB";
    }
    private async Task<IReadOnlyList<string>> GetHackerNewsHeadlinesAsync(CancellationToken cancellationToken)
    {
        const string url = "https://hn.algolia.com/api/v1/search?tags=front_page&hitsPerPage=5";
        var response = await _httpClient.GetFromJsonAsync<HackerNewsResponse>(url, cancellationToken);

        var headlines = response?.Hits
            ?.Select(hit => hit.Title ?? hit.StoryTitle)
            ?.Where(title => !string.IsNullOrWhiteSpace(title))
            ?.Select(title => title!.Trim())
            ?.Take(5)
            ?.ToList();

        if (headlines is null || headlines.Count == 0)
        {
            return ["новости недоступны"];
        }

        return headlines;
    }

    private async Task<IReadOnlyList<string>> GetSpaceflightHeadlinesAsync(CancellationToken cancellationToken)
    {
        const string url = "https://api.spaceflightnewsapi.net/v4/articles/?limit=5";
        var response = await _httpClient.GetFromJsonAsync<SpaceflightResponse>(url, cancellationToken);

        var headlines = response?.Results
            ?.Select(article => article.Title)
            ?.Where(title => !string.IsNullOrWhiteSpace(title))
            ?.Select(title => title.Trim())
            ?.Take(5)
            ?.ToList();

        if (headlines is null || headlines.Count == 0)
        {
            return ["новости недоступны"];
        }

        return headlines;
    }
}

internal static partial class TextAnalyzer
{
    public static IReadOnlyList<KeyValuePair<string, int>> FindTopWordsParallel(
        IReadOnlyList<string> lines,
        int topCount,
        CancellationToken cancellationToken)
    {
        var frequencies = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        Parallel.ForEach(
            lines,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            },
            line =>
            {
                foreach (var word in ExtractWords(line))
                {
                    if (word.Length < 4)
                    {
                        continue;
                    }

                    frequencies.AddOrUpdate(word, 1, (_, count) => count + 1);
                }
            });

        return frequencies
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(topCount)
            .ToList();
    }

    private static IEnumerable<string> ExtractWords(string text)
    {
        foreach (Match match in WordRegex().Matches(text.ToLowerInvariant()))
        {
            yield return match.Value;
        }
    }

    [GeneratedRegex("[\\p{L}\\p{Nd}]+", RegexOptions.Compiled)]
    private static partial Regex WordRegex();
}

internal sealed record DashboardResult(
    string WeatherSummary,
    string RatesSummary,
    IReadOnlyList<string> HackerNewsHeadlines,
    IReadOnlyList<string> SpaceflightHeadlines);

internal sealed class WeatherApiResponse
{
    public WeatherCurrent? Current { get; set; }
}

internal sealed class WeatherCurrent
{
    [System.Text.Json.Serialization.JsonPropertyName("temperature_2m")]
    public double Temperature2m { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("wind_speed_10m")]
    public double WindSpeed10m { get; set; }
}

internal sealed class RatesApiResponse
{
    public Dictionary<string, double> Rates { get; set; } = new();
}

internal sealed class CbrDailyResponse
{
    public CbrValute? Valute { get; set; }
}

internal sealed class CbrValute
{
    [System.Text.Json.Serialization.JsonPropertyName("USD")]
    public CbrCurrency? Usd { get; set; }
}

internal sealed class CbrCurrency
{
    [System.Text.Json.Serialization.JsonPropertyName("Value")]
    public double Value { get; set; }
}

internal sealed class HackerNewsResponse
{
    public List<HackerNewsHit> Hits { get; set; } = [];
}

internal sealed class HackerNewsHit
{
    public string? Title { get; set; }
    public string? StoryTitle { get; set; }
}

internal sealed class SpaceflightResponse
{
    public List<SpaceflightArticle> Results { get; set; } = [];
}

internal sealed class SpaceflightArticle
{
    public string Title { get; set; } = string.Empty;
}


