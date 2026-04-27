using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using EnvDataCollector.Data.Repositories;
using Newtonsoft.Json.Linq;
using NLog;

namespace EnvDataCollector.Services
{
    public sealed class ImageUploadService
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly AppSettingRepository _settings = new();

        private static readonly HttpClient _http = new HttpClient();

        public string Upload(byte[] imageData, string fileName = "image.jpg")
        {
            string url = _settings.Get(SK.ImageUploadUrl);
            if (string.IsNullOrWhiteSpace(url))
            {
                Log.Debug("ImageUploadUrl 未配置，跳过上传");
                return null;
            }

            try
            {
                using var content = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(imageData);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
                content.Add(fileContent, "file", fileName);

                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

                var resp = _http.SendAsync(req).GetAwaiter().GetResult();
                string respText = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!resp.IsSuccessStatusCode)
                {
                    Log.Warn("图片上传失败 HTTP {0}：{1}", (int)resp.StatusCode,
                        respText?.Substring(0, Math.Min(200, respText?.Length ?? 0)));
                    return null;
                }

                string remoteUrl = ParseUrlFromResponse(respText);
                if (!string.IsNullOrEmpty(remoteUrl))
                    Log.Debug("图片上传成功：{0}", remoteUrl);
                else
                    Log.Warn("图片上传响应中未找到 URL：{0}",
                        respText?.Substring(0, Math.Min(200, respText?.Length ?? 0)));

                return remoteUrl;
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "图片上传异常");
                return null;
            }
        }

        public string UploadFromLocal(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;

            string fullPath = Path.IsPathRooted(relativePath)
                ? relativePath
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);

            if (!File.Exists(fullPath))
            {
                Log.Warn("本地图片不存在：{0}", fullPath);
                return null;
            }

            try
            {
                byte[] data = File.ReadAllBytes(fullPath);
                string fileName = Path.GetFileName(fullPath);
                return Upload(data, fileName);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "读取本地图片失败：{0}", fullPath);
                return null;
            }
        }

        private static string ParseUrlFromResponse(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                string url = FindString(root, "url", "fileUrl", "file_url", "path",
                    "imageUrl", "image_url", "data", "src", "link");
                if (!string.IsNullOrEmpty(url)) return url;

                var dataToken = root["data"];
                if (dataToken != null && dataToken.Type == JTokenType.String)
                    return dataToken.ToString();

                return null;
            }
            catch
            {
                return json?.Trim().StartsWith("http") == true ? json.Trim() : null;
            }
        }

        private static string FindString(JObject root, params string[] names)
        {
            foreach (var name in names)
            {
                var t = FindToken(root, name);
                if (t != null && t.Type != JTokenType.Null)
                {
                    string s = t.ToString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            return null;
        }

        private static JToken FindToken(JToken node, string name, int depth = 3)
        {
            if (node is JObject obj)
            {
                if (obj.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var v)) return v;
                if (depth <= 0) return null;
                foreach (var p in obj.Properties())
                {
                    var found = FindToken(p.Value, name, depth - 1);
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
