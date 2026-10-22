using System.Net.Http;
using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

public class BadHost : AedisApiHost
{
    // ruleid: aedis.enable-authentication-false
    protected override bool EnableAuthentication => false;
    // ruleid: aedis.swagger-enabled-unconditionally
    protected override bool EnableSwagger => true;
}

public class GoodHost : AedisApiHost
{
    // ok: aedis.enable-authentication-false
    protected override bool EnableAuthentication => true;
    // ok: aedis.swagger-enabled-unconditionally
    protected override bool EnableSwagger => Environment.IsDevelopment();
    // ok: aedis.swagger-enabled-unconditionally
    // protected override bool EnableSwagger => true;
}

public class OrdersController : ControllerBase
{
    // ruleid: aedis.allow-anonymous-on-write-verb
    [AllowAnonymous]
    [HttpPost("orders")]
    public IActionResult Create() { return Ok(); }

    // ruleid: aedis.allow-anonymous-on-write-verb
    [HttpDelete]
    [AllowAnonymous]
    public IActionResult Remove() { return Ok(); }

    // ok: aedis.allow-anonymous-on-write-verb
    [AllowAnonymous]
    [HttpGet("orders")]
    public IActionResult List() { return Ok(); }

    // ok: aedis.allow-anonymous-on-write-verb
    [Authorize]
    [HttpPost("orders/approve")]
    public IActionResult Approve() { return Ok(); }
}

public class Repo
{
    public void Queries(System.Data.IDbConnection conn, string id, string name)
    {
        // ruleid: aedis.dapper-sql-interpolation
        conn.Query<int>($"select * from orders where id = {id}");
        // ruleid: aedis.dapper-sql-interpolation
        conn.ExecuteAsync("delete from orders where name = '" + name + "'");
        // ok: aedis.dapper-sql-interpolation
        conn.Query<int>("select * from orders where id = @id", new { id });
        // ok: aedis.dapper-sql-interpolation
        conn.Execute("update orders set x = @x", new { x = name });
    }

    public void Tls(HttpClientHandler handler)
    {
        // ruleid: aedis.tls-validation-disabled
        handler.ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;
        // ruleid: aedis.tls-validation-disabled
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        // ok: aedis.tls-validation-disabled
        handler.ServerCertificateCustomValidationCallback = (m, c, ch, e) => e == System.Net.Security.SslPolicyErrors.None;
    }

    public void Deserialize(byte[] data)
    {
        // ruleid: aedis.insecure-deserialization
        var f = new BinaryFormatter();
        // ruleid: aedis.insecure-deserialization
        var s = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };
        // ok: aedis.insecure-deserialization
        var t = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None };
    }

    public void Logs(ILogger log, string password, string apiKey, string orderId, Options o)
    {
        // ruleid: aedis.sensitive-data-in-log
        log.LogInformation("login {Password}", password);
        // ruleid: aedis.sensitive-data-in-log
        log.LogDebug("key {Key}", o.ApiKey);
        // ok: aedis.sensitive-data-in-log
        log.LogInformation("order {OrderId}", orderId);
    }

    public void Cors(CorsPolicyBuilder b)
    {
        // ruleid: aedis.cors-any-origin-with-credentials
        b.AllowAnyOrigin().AllowAnyHeader().AllowCredentials();
        // ok: aedis.cors-any-origin-with-credentials
        b.WithOrigins("https://app.example.com").AllowCredentials();
    }

    public IActionResult Redirects(string returnUrl, HttpContext ctx)
    {
        // ruleid: aedis.open-redirect
        ctx.Response.Redirect(returnUrl);
        // ok: aedis.open-redirect
        ctx.Response.Redirect("/home");
        if (Url.IsLocalUrl(returnUrl))
        {
            // ok: aedis.open-redirect
            return Redirect(returnUrl);
        }
        // ok: aedis.open-redirect
        return LocalRedirect(returnUrl);
    }

    public IActionResult RedirectAway(string next)
    {
        // ruleid: aedis.open-redirect
        return Redirect(next);
    }

    public void Cookies(HttpResponse r)
    {
        // ruleid: aedis.cookie-missing-flags
        r.Cookies.Append("sid", "v", new CookieOptions { HttpOnly = true });
        // ruleid: aedis.cookie-missing-flags
        var plain = new CookieOptions();
        // ok: aedis.cookie-missing-flags
        r.Cookies.Append("sid", "v", new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict });
        // ok: aedis.cookie-missing-flags
        var lax = new CookieOptions { SameSite = SameSiteMode.Lax, HttpOnly = true, Secure = true, Path = "/" };
    }

    public void Jwt(JwtBearerOptions o)
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            // ruleid: aedis.jwt-validation-disabled
            ValidateAudience = false,
            // ruleid: aedis.jwt-validation-disabled
            RequireSignedTokens = false,
            // ok: aedis.jwt-validation-disabled
            ValidateIssuer = true,
        };
    }

    public void ErrorPages(WebApplication app, IWebHostEnvironment env)
    {
        // ruleid: aedis.developer-exception-page
        app.UseDeveloperExceptionPage();
        if (env.IsDevelopment())
        {
            // ok: aedis.developer-exception-page
            app.UseDeveloperExceptionPage();
        }
    }
}
