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

// ����HttpClient
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

// URL�����ת������
app.Map("/{*url}", async (HttpContext httpContext, IHttpClientFactory httpClientFactory, string url) =>
{
    string referer = httpContext.Request.Headers.Referer.ToString();

    // ��� referer �� url �����Ϸ��򷵻ش���
    if (string.IsNullOrEmpty(referer) && !url.StartsWith("http"))
    {
        httpContext.Response.StatusCode = 400;
        await httpContext.Response.WriteAsync("URL is invalid!");
        return;
    }

    // �������URL
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
        // ��ȡĿ������
        var match = Regex.Match(url, @"(https?://[^/]+)");
        var domain = match.Success ? match.Groups[1].Value : "https://google.com";
        var isRedirect = (domain == "https://github.com" && !url.Contains("archive") && !url.Contains("releases"));

        using var httpClient = httpClientFactory.CreateClient(isRedirect ? ClientRedirectName : ClientName);

        // ��������ͷ
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

        // �����GET���������
        if (httpContext.Request.QueryString.HasValue)
        {
            url += httpContext.Request.QueryString.Value;
        }

        // ��������
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Parse(httpContext.Request.Method), url);

        // ��GET����Я��������
        if (httpContext.Request.Method.ToUpper() != "GET")
        {
            var content = new StreamContent(httpContext.Request.Body);
            if (httpContext.Request.Headers.ContainsKey("Content-Type"))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue(httpContext.Request.Headers["Content-Type"]!);
            }
            request.Content = content;
        }

        // �������󲢴�����Ӧ
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        // ����Ƿ�ʹ���˷ֿ鴫�����
        bool isChunked = response.Headers.TransferEncodingChunked.GetValueOrDefault(false);

        // ת����Ӧͷ
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            if (header.Key != "Transfer-Encoding" || !isChunked)
            {
                httpContext.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        httpContext.Response.StatusCode = (int)response.StatusCode;
        httpContext.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/html;charset=utf-8";

        // �ֿ������Ӧ
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
