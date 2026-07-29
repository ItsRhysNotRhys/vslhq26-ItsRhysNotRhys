using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace LegacyApp
{
    // This file is intentionally "clean": no Thread.Sleep, no blocking .Result/.Wait(),
    // and no ArrayList/Hashtable. It exists to prove the engine correctly reports
    // "No anti-patterns found." and returns WITHOUT calling OpenAI or Context7 -
    // i.e. it doesn't waste any tokens/API calls when a file has nothing to modernize.
    class OrderProcessor
    {
        private readonly HttpClient _httpClient = new HttpClient();

        public async Task<string> FetchOrderStatusAsync(string orderId)
        {
            // Modern pattern: properly awaited async call, no blocking .Result/.Wait()
            HttpResponseMessage response = await _httpClient.GetAsync($"https://example.com/orders/{orderId}");
            return await response.Content.ReadAsStringAsync();
        }

        public string BuildReport(IReadOnlyList<string> items, IReadOnlyDictionary<string, double> prices)
        {
            // Modern pattern: generic collections, StringBuilder instead of string concatenation
            var report = new StringBuilder();
            foreach (string item in items)
            {
                report.AppendLine($"{item}: ${prices[item]}");
            }

            return report.ToString();
        }

        public async Task ProcessAsync()
        {
            // Modern pattern: non-blocking delay via await, not Thread.Sleep
            await Task.Delay(100);
            Console.WriteLine("Processing complete.");
        }
    }

    class Program
    {
        static async Task Main()
        {
            var processor = new OrderProcessor();

            var items = new List<string> { "Widget", "Gadget", "Gizmo" };
            var prices = new Dictionary<string, double>
            {
                ["Widget"] = 9.99,
                ["Gadget"] = 14.99,
                ["Gizmo"] = 4.99
            };

            Console.WriteLine(processor.BuildReport(items, prices));

            await processor.ProcessAsync();
        }
    }
}
