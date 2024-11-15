using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
if (!Debugger.IsAttached)
{
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(8008);
    });
}

const string ClientName = "client";
const string ClientRedirectName = "clientRedirect";

// 配置HttpClient
builder.Services.AddHttpClient(ClientName, x =>
{
    x.Timeout = TimeSpan.FromSeconds(3.0);
    x.MaxResponseContentBufferSize = 8192;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    Proxy = new WebProxy("127.0.0.1", 1080),
    UseProxy = true,
    UseCookies = true,
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.All,
    MaxResponseHeadersLength = 8192,
    MaxRequestContentBufferSize = 8192,
    CheckCertificateRevocationList = true,
    MaxConnectionsPerServer = 50000,
});

builder.Services.AddHttpClient(ClientRedirectName, x =>
{
    x.Timeout = TimeSpan.FromSeconds(3.0);
    x.MaxResponseContentBufferSize = 8192;
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    UseCookies = true,
    AllowAutoRedirect = true,
    AutomaticDecompression = DecompressionMethods.All,
    MaxResponseHeadersLength = 8192,
    MaxRequestContentBufferSize = 8192,
    CheckCertificateRevocationList = true,
    MaxConnectionsPerServer = 50000,
});

WebApplication app = builder.Build();

// URL处理和转发请求
app.Map("/{*url}", async (HttpContext httpContext, IHttpClientFactory httpClientFactory, string url) =>
{
    string referer = httpContext.Request.Headers.Referer.ToString();

    // 如果 referer 和 url 都不合法则返回错误
    if (string.IsNullOrEmpty(referer) && !url.StartsWith("http"))
    {
        httpContext.Response.StatusCode = 400;
        await httpContext.Response.WriteAsync("URL is invalid!");
        return;
    }

    // 处理相对URL
    if (!string.IsNullOrEmpty(referer) && !url.StartsWith("http"))
    {
        var match = Regex.Match(referer, @"\/(https?://[^/]+)");
        var domain = match.Success ? match.Groups[1].Value : string.Empty;
        if (!string.IsNullOrEmpty(domain))
        {
            url = $"{domain}/{url}";
        }
    }

    try
    {
        // 获取目标域名
        var match = Regex.Match(url, @"(https?://[^/]+)");
        var domain = match.Success ? match.Groups[1].Value : "https://google.com";
        var isRedirect = (domain == "https://github.com" && !url.Contains("archive") && !url.Contains("releases"));

        using var httpClient = httpClientFactory.CreateClient(isRedirect ? ClientRedirectName : ClientName);

        // 设置请求头
        foreach (var header in httpContext.Request.Headers)
        {
            if (domain == "https://github.com" && header.Key == "Host")
                continue;

            if (header.Key == "Host")
            {
                httpClient.DefaultRequestHeaders.Host = domain.Replace("https://", "").Replace("http://", "");
                continue;
            }

            if (header.Key == "Referer")
            {
                httpClient.DefaultRequestHeaders.Referrer = new Uri(url.StartsWith("http") ? url : domain);
                continue;
            }

            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        // 如果有GET参数则加上
        if (httpContext.Request.QueryString.HasValue)
        {
            url += httpContext.Request.QueryString.Value;
        }

        // 构建请求
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Parse(httpContext.Request.Method), url);

        // 非GET请求携带请求体
        if (httpContext.Request.Method.ToUpper() != "GET")
        {
            var content = new StreamContent(httpContext.Request.Body);
            if (httpContext.Request.Headers.ContainsKey("Content-Type"))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue(httpContext.Request.Headers["Content-Type"]!);
            }
            request.Content = content;
        }

        // 发送请求并处理响应
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        // 检查是否使用了分块传输编码
        bool isChunked = response.Headers.TransferEncodingChunked.GetValueOrDefault(false);

        // 转发响应头
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            if (header.Key != "Transfer-Encoding" || !isChunked)
            {
                httpContext.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        httpContext.Response.StatusCode = (int)response.StatusCode;
        httpContext.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/html;charset=utf-8";

        // 分块编码响应
        if (isChunked)
        {
            await response.Content.CopyToAsync(httpContext.Response.Body);
        }
        else
        {
            using Stream stream = await response.Content.ReadAsStreamAsync();
            await stream.CopyToAsync(httpContext.Response.Body);
        }
    }
    catch (Exception ex)
    {
        httpContext.Response.Clear();
        httpContext.Response.StatusCode = 500;
        await httpContext.Response.WriteAsync($"Load page error: {ex.Message}");
    }
});

app.Run();
