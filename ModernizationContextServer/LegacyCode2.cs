using System;
using System.Net.Http;

namespace LegacyApp
{
    class DownloadService
    {
        private readonly HttpClient _httpClient = new HttpClient();

        public string FetchPage(string url)
        {
            // Anti-pattern: Blocking on an async call with .Result, risking thread-pool
            // starvation and deadlocks in synchronization-context-aware apps.
            HttpResponseMessage response = _httpClient.GetAsync(url).Result;
            string content = response.Content.ReadAsStringAsync().Result;
            return content;
        }

        public void LogStatus(string message)
        {
            Console.WriteLine($"[LOG] {message}");
        }
    }

    class Program
    {
        static void Main()
        {
            var service = new DownloadService();
            string html = service.FetchPage("https://example.com");
            service.LogStatus($"Downloaded {html.Length} characters.");
        }
    }
}
