using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
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
            Console.WriteLine($"Погода: {dashboard.WeatherSummary}");
            Console.WriteLine($"Курсы: {dashboard.RatesSummary}");
            Console.WriteLine("Новости:");
            foreach (var headline in dashboard.Headlines)
            {
                Console.WriteLine($"- {headline}");
            }

            Console.WriteLine("\n=== Параллельный анализ текста ===");
            var topWords = TextAnalyzer.FindTopWordsParallel(dashboard.Headlines, 8, cts.Token);
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
        var weatherTask = GetWeatherSummaryAsync(cancellationToken);
        var ratesTask = GetRatesSummaryAsync(cancellationToken);
        var headlinesTask = GetHeadlinesAsync(cancellationToken);

        await Task.WhenAll(weatherTask, ratesTask, headlinesTask);

        return new DashboardResult(
            weatherTask.Result,
            ratesTask.Result,
            headlinesTask.Result);
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
        const string url = "https://api.frankfurter.app/latest?from=USD&to=EUR,RUB";
        var rateResponse = await _httpClient.GetFromJsonAsync<RatesApiResponse>(url, cancellationToken);

        if (rateResponse?.Rates is null)
        {
            return "нет данных";
        }

        var eurText = rateResponse.Rates.TryGetValue("EUR", out var eur)
            ? eur.ToString("F3")
            : "n/a";

        var rubText = rateResponse.Rates.TryGetValue("RUB", out var rub)
            ? rub.ToString("F3")
            : "n/a";

        return $"1 USD = {eurText} EUR, {rubText} RUB";
    }

    private async Task<IReadOnlyList<string>> GetHeadlinesAsync(CancellationToken cancellationToken)
    {
        const string url = "https://jsonplaceholder.typicode.com/posts?_limit=8";
        var posts = await _httpClient.GetFromJsonAsync<List<PostDto>>(url, cancellationToken);

        if (posts is null || posts.Count == 0)
        {
            return new[] { "новости недоступны" };
        }

        return posts
            .Select(p => p.Title)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title.Trim())
            .ToList();
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
    IReadOnlyList<string> Headlines);

internal sealed class PostDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

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
