/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace de4dot.code.Ollama {
    /// <summary>
    /// Клиент для подключения к Ollama API
    /// </summary>
    public class OllamaClient : IDisposable {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _model;
        private bool _disposed;

        /// <summary>
        /// Конфигурация клиента Ollama
        /// </summary>
        public class OllamaConfig {
            public string BaseUrl { get; set; } = "http://localhost:11434";
            public string Model { get; set; } = "llama3";
            public int TimeoutSeconds { get; set; } = 120;
        }

        /// <summary>
        /// Запрос к Ollama API
        /// </summary>
        public class OllamaRequest {
            [JsonProperty("model")]
            public string Model { get; set; }

            [JsonProperty("prompt")]
            public string Prompt { get; set; }

            [JsonProperty("stream")]
            public bool Stream { get; set; } = false;

            [JsonProperty("options")]
            public OllamaOptions Options { get; set; }
        }

        /// <summary>
        /// Опции генерации
        /// </summary>
        public class OllamaOptions {
            [JsonProperty("temperature")]
            public double? Temperature { get; set; }

            [JsonProperty("top_p")]
            public double? TopP { get; set; }

            [JsonProperty("num_predict")]
            public int? NumPredict { get; set; }
        }

        /// <summary>
        /// Ответ от Ollama API
        /// </summary>
        public class OllamaResponse {
            [JsonProperty("model")]
            public string Model { get; set; }

            [JsonProperty("created_at")]
            public string CreatedAt { get; set; }

            [JsonProperty("response")]
            public string Response { get; set; }

            [JsonProperty("done")]
            public bool Done { get; set; }

            [JsonProperty("error")]
            public string Error { get; set; }
        }

        /// <summary>
        /// Создает новый экземпляр клиента Ollama
        /// </summary>
        public OllamaClient(OllamaConfig config = null) {
            config = config ?? new OllamaConfig();
            _baseUrl = config.BaseUrl.TrimEnd('/');
            _model = config.Model;

            _httpClient = new HttpClient {
                BaseAddress = new Uri(_baseUrl),
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };
            _httpClient.DefaultRequestHeaders.Accept.Clear();
            _httpClient.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        }

        /// <summary>
        /// Проверяет доступность сервера Ollama
        /// </summary>
        public async Task<bool> IsAvailableAsync() {
            try {
                var response = await _httpClient.GetAsync("/api/tags");
                return response.IsSuccessStatusCode;
            }
            catch {
                return false;
            }
        }

        /// <summary>
        /// Получает список доступных моделей
        /// </summary>
        public async Task<string[]> GetModelsAsync() {
            try {
                var response = await _httpClient.GetAsync("/api/tags");
                if (response.IsSuccessStatusCode) {
                    var content = await response.Content.ReadAsStringAsync();
                    var result = JsonConvert.DeserializeObject<ModelsResponse>(content);
                    return result?.Models?.ConvertAll(m => m.Name).ToArray() ?? new string[0];
                }
            }
            catch {
                // Игнорируем ошибки
            }
            return new string[0];
        }

        /// <summary>
        /// Отправляет запрос к модели и получает ответ
        /// </summary>
        public async Task<string> GenerateAsync(string prompt, OllamaOptions options = null) {
            var request = new OllamaRequest {
                Model = _model,
                Prompt = prompt,
                Stream = false,
                Options = options ?? new OllamaOptions()
            };

            var json = JsonConvert.SerializeObject(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/generate", content);
            if (response.IsSuccessStatusCode) {
                var responseContent = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<OllamaResponse>(responseContent);
                
                if (result != null) {
                    if (!string.IsNullOrEmpty(result.Error))
                        throw new Exception($"Ollama error: {result.Error}");
                    return result.Response;
                }
            }
            
            throw new Exception($"Ollama request failed: {response.StatusCode}");
        }

        /// <summary>
        /// Анализирует код и возвращает рекомендации
        /// </summary>
        public async Task<string> AnalyzeCodeAsync(string code, string analysisType = "deobfuscation") {
            string prompt;
            
            switch (analysisType.ToLower()) {
                case "deobfuscation":
                    prompt = $@"Analyze this .NET code and provide deobfuscation recommendations:
- Identify obfuscation techniques used
- Suggest specific deobfuscation steps
- Point out string encryption methods
- Identify control flow obfuscation

Code:
{code}

Provide a concise technical analysis.";
                    break;
                    
                case "cleanup":
                    prompt = $@"Analyze this .NET code and suggest cleanup operations:
- Identify dead code
- Find unused methods and fields
- Suggest metadata fixes
- Recommend resource cleanup

Code:
{code}

Provide specific cleanup recommendations.";
                    break;
                    
                default:
                    prompt = $@"Analyze this .NET code:
{code}

Provide technical analysis.";
                    break;
            }

            var options = new OllamaOptions {
                Temperature = 0.3,
                TopP = 0.9,
                NumPredict = 2048
            };

            return await GenerateAsync(prompt, options);
        }

        /// <summary>
        /// Синхронная версия GenerateAsync
        /// </summary>
        public string Generate(string prompt, OllamaOptions options = null) {
            return GenerateAsync(prompt, options).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Синхронная версия AnalyzeCodeAsync
        /// </summary>
        public string AnalyzeCode(string code, string analysisType = "deobfuscation") {
            return AnalyzeCodeAsync(code, analysisType).GetAwaiter().GetResult();
        }

        protected virtual void Dispose(bool disposing) {
            if (!_disposed) {
                if (disposing) {
                    _httpClient?.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Ответ API со списком моделей
        /// </summary>
        private class ModelsResponse {
            [JsonProperty("models")]
            public System.Collections.Generic.List<ModelInfo> Models { get; set; }
        }

        private class ModelInfo {
            [JsonProperty("name")]
            public string Name { get; set; }
        }
    }

    /// <summary>
    /// Статический класс для удобного доступа к Ollama
    /// </summary>
    public static class OllamaService {
        private static OllamaClient _instance;
        private static readonly object _lock = new object();

        /// <summary>
        /// Получает или задает конфигурацию по умолчанию
        /// </summary>
        public static OllamaClient.OllamaConfig DefaultConfig { get; set; } = new OllamaClient.OllamaConfig();

        /// <summary>
        /// Получает экземпляр клиента Ollama
        /// </summary>
        public static OllamaClient Instance {
            get {
                lock (_lock) {
                    if (_instance == null) {
                        _instance = new OllamaClient(DefaultConfig);
                    }
                    return _instance;
                }
            }
        }

        /// <summary>
        /// Проверяет доступность Ollama
        /// </summary>
        public static bool IsAvailable() {
            try {
                return Instance.IsAvailableAsync().Result;
            }
            catch {
                return false;
            }
        }

        /// <summary>
        /// Анализирует код с помощью Ollama
        /// </summary>
        public static string AnalyzeCode(string code, string analysisType = "deobfuscation") {
            return Instance.AnalyzeCode(code, analysisType);
        }

        /// <summary>
        /// Отправляет произвольный запрос к Ollama
        /// </summary>
        public static string Generate(string prompt) {
            return Instance.Generate(prompt);
        }
    }
}
