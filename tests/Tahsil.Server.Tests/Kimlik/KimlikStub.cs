using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Tahsil.Application.Kimlik;
using Tahsil.Infrastructure.Kimlik;

namespace Tahsil.Server.Tests.Kimlik;

/// <summary>
/// Kimlik's token endpoint and Management API as the connector uses them, in memory: the requests and answers of
/// Kimlik 0.1.0's contracts (camelCase JSON, enums as camelCase names, problem details with a <c>code</c>) and its
/// subscription rules (one current subscription per subscriber, trials converted by status, cancellation at the
/// period's end or expiry at once, and expiry when the trial or period is over). Every request is recorded, failures
/// can be queued, and tokens revoked.
/// </summary>
public sealed class KimlikStub(TimeProvider time) : HttpMessageHandler
{
    public const string ClientId = "tahsil";
    public const string ClientSecret = "kimlik-client-secret";

    public static readonly Uri Authority = new("https://kimlik.test/");

    public static readonly string WebhookSecret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly Lock _gate = new();
    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
    private readonly Queue<HttpStatusCode> _failures = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>The settings that point Tahsil's connector at this stub.</summary>
    public static Dictionary<string, string?> Settings => new()
    {
        ["Tahsil:Kimlik:Enabled"] = "true",
        ["Tahsil:Kimlik:Authority"] = Authority.AbsoluteUri,
        ["Tahsil:Kimlik:ClientId"] = ClientId,
        ["Tahsil:Kimlik:ClientSecret"] = ClientSecret,
        ["Tahsil:Kimlik:WebhookSecret"] = WebhookSecret,
    };

    /// <summary>Every request but the token's, in order.</summary>
    public List<Recorded> Requests { get; } = [];

    public int TokensIssued { get; private set; }

    public Dictionary<Guid, User> Users { get; } = [];

    public Dictionary<Guid, string> Organizations { get; } = [];

    public Dictionary<string, Plan> Plans { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether every API request fails, as during an outage.</summary>
    public bool Down { get; set; }

    /// <summary>Whether the token endpoint accepts Tahsil's credentials.</summary>
    public bool AcceptsCredentials { get; set; } = true;

    public IReadOnlyList<Subscription> Subscriptions
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscriptions];
            }
        }
    }

    /// <summary>Routes the connector's HTTP client to this stub.</summary>
    public void Register(IServiceCollection services) => services.AddHttpClient(KimlikClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => this);

    public Guid AddUser(KimlikUserStatus status = KimlikUserStatus.Active, params string[] roles)
    {
        var id = Guid.CreateVersion7();
        Users[id] = new User(id, status, roles);
        return id;
    }

    public Guid AddOrganization(string slug)
    {
        var id = Guid.CreateVersion7();
        Organizations[id] = slug;
        return id;
    }

    public void AddPlan(string key, string kind = "base", bool archived = false) => Plans[key] = new Plan(key, kind, archived);

    /// <summary>Deletes a user and, silently, its subscriptions, as Kimlik does.</summary>
    public void DeleteUser(Guid id)
    {
        lock (_gate)
        {
            Users.Remove(id);
            _subscriptions.RemoveAll(subscription => subscription.SubscriberType == "user" && subscription.SubscriberId == id);
        }
    }

    /// <summary>
    /// A webhook as Kimlik sends it (Kimlik.Contracts.Webhooks.WebhookEvent), signed with Standard Webhooks at
    /// <paramref name="sentAt"/> with <paramref name="secret"/>, the endpoint's secret unless another is given.
    /// </summary>
    public static HttpRequestMessage Webhook(
        string type, string subjectType, Guid subjectId, string actorType, string? actorId, DateTimeOffset sentAt, string? secret = null, string? tamperedBody = null)
    {
        var body = new JsonObject
        {
            ["type"] = type,
            ["timestamp"] = sentAt,
            ["data"] = new JsonObject
            {
                ["subjectType"] = subjectType,
                ["subjectId"] = subjectId.ToString(),
                ["organizationId"] = null,
                ["actor"] = new JsonObject { ["type"] = actorType, ["id"] = actorId },
                ["details"] = null,
            },
        }.ToJsonString();
        var id = $"msg_{Guid.CreateVersion7():N}";
        var timestamp = sentAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var key = Convert.FromBase64String((secret ?? WebhookSecret)["whsec_".Length..]);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/webhooks/kimlik", UriKind.Relative))
        {
            Content = new StringContent(tamperedBody ?? body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("webhook-id", id);
        request.Headers.Add("webhook-timestamp", timestamp);
        request.Headers.Add("webhook-signature", $"v1,{signature}");
        return request;
    }

    /// <summary>The next API responses fail with these statuses, one each.</summary>
    public void Fail(params HttpStatusCode[] statuses)
    {
        lock (_gate)
        {
            foreach (var status in statuses)
            {
                _failures.Enqueue(status);
            }
        }
    }

    /// <summary>Tokens issued so far stop working, as after a signing key rotation.</summary>
    public void RevokeTokens()
    {
        lock (_gate)
        {
            _tokens.Clear();
        }
    }

    /// <summary>A subscription made in Kimlik by someone else, such as a Kimlik administrator.</summary>
    public Subscription Seed(Guid subscriberId, bool organization, string plan, DateTimeOffset? currentPeriodEnd, string? externalReference = null)
    {
        lock (_gate)
        {
            var subscription = new Subscription(Guid.CreateVersion7(), organization ? "organization" : "user", subscriberId, plan, time.GetUtcNow())
            {
                Status = "active",
                CurrentPeriodEnd = currentPeriodEnd,
                ExternalReference = externalReference,
            };
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/connect/token")
            {
                return Token(request, body);
            }

            Requests.Add(new Recorded(request.Method.Method, request.RequestUri.PathAndQuery, body));
            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } token } || !_tokens.Contains(token))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (Down)
            {
                return Problem(HttpStatusCode.ServiceUnavailable, "server.error", "Kimlik is down.");
            }

            if (_failures.TryDequeue(out var failure))
            {
                return Problem(failure, "server.error", "Kimlik failed.");
            }

            var now = time.GetUtcNow();
            foreach (var subscription in _subscriptions)
            {
                subscription.ExpireIfEnded(now);
            }

            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return (request.Method.Method, segments) switch
            {
                ("GET", ["api", "v1", "subscriptions"]) => ListSubscriptions(query["subscriberType"], query["subscriberId"]),
                ("POST", ["api", "v1", "subscriptions"]) => Create(JsonNode.Parse(body!)!.AsObject(), now),
                ("GET", ["api", "v1", "subscriptions", var id]) => Find(id) is { } found ? Ok(found.ToJson()) : SubscriptionNotFound(),
                ("PATCH", ["api", "v1", "subscriptions", var id]) => Find(id) is { } found ? Update(found, JsonNode.Parse(body!)!.AsObject()) : SubscriptionNotFound(),
                ("POST", ["api", "v1", "subscriptions", var id, "cancel"]) => Find(id) is { } found ? Cancel(found, now) : SubscriptionNotFound(),
                ("GET", ["api", "v1", "plans"]) => ListPlans(int.Parse(query["limit"] ?? "50", CultureInfo.InvariantCulture), query["cursor"]),
                ("GET", ["api", "v1", "users", var id]) => Guid.TryParse(id, out var userId) && Users.TryGetValue(userId, out var user)
                    ? Ok(new JsonObject
                    {
                        ["id"] = user.Id,
                        ["email"] = $"{user.Id:N}@example.com",
                        ["emailVerified"] = true,
                        ["status"] = Wire(user.Status),
                        ["roles"] = new JsonArray([.. user.Roles.Select(role => JsonValue.Create(role))]),
                    })
                    : Problem(HttpStatusCode.NotFound, "user.not_found", "The user does not exist."),
                ("GET", ["api", "v1", "organizations", var id]) => Guid.TryParse(id, out var organizationId) && Organizations.TryGetValue(organizationId, out var slug)
                    ? Ok(new JsonObject { ["id"] = organizationId, ["name"] = slug, ["slug"] = slug, ["requireMfa"] = false })
                    : Problem(HttpStatusCode.NotFound, "organization.not_found", "The organization does not exist."),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }
    }

    private static HttpResponseMessage Ok(JsonNode body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body, options: Json) };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail) => new(status)
    {
        Content = JsonContent.Create(
            new JsonObject { ["type"] = "about:blank", ["title"] = detail, ["status"] = (int)status, ["detail"] = detail, ["code"] = code },
            mediaType: new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json")),
    };

    private static HttpResponseMessage SubscriptionNotFound() => Problem(HttpStatusCode.NotFound, "subscription.not_found", "The subscription does not exist.");

    private static HttpResponseMessage Invalid() =>
        Problem(HttpStatusCode.BadRequest, "subscription.invalid", "A trial needs an end, a period must end after it starts, and an external reference is at most 200 characters.");

    private static string Wire<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    /// <summary>An instant as Kimlik reads it: UTC with <c>Z</c> only, since other offsets fail in Kimlik's database.</summary>
    private static bool TryReadTime(JsonObject body, string name, out DateTimeOffset? value, out bool given)
    {
        value = null;
        given = body.ContainsKey(name);
        if (!given || body[name] is null)
        {
            return true;
        }

        var text = body[name]!.GetValue<string>();
        if (!text.EndsWith('Z') || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private HttpResponseMessage Token(HttpRequestMessage request, string? body)
    {
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
        if (!AcceptsCredentials || request.Headers.Authorization is not { Scheme: "Basic" } basic || basic.Parameter != expected
            || body?.Contains("grant_type=client_credentials", StringComparison.Ordinal) != true || !body.Contains("scope=kimlik", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_client" }) };
        }

        TokensIssued++;
        var token = $"kimlik-token-{TokensIssued.ToString(CultureInfo.InvariantCulture)}";
        _tokens.Add(token);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = token, token_type = "Bearer", expires_in = 600 }) };
    }

    private Subscription? Find(string id) => Guid.TryParse(id, out var parsed) ? _subscriptions.SingleOrDefault(subscription => subscription.Id == parsed) : null;

    private HttpResponseMessage ListSubscriptions(string? type, string? subscriber)
    {
        var items = _subscriptions
            .Where(subscription => subscriber is null || (subscription.SubscriberType == type && subscription.SubscriberId == Guid.Parse(subscriber)))
            .OrderByDescending(subscription => subscription.Created)
            .ThenByDescending(subscription => subscription.Id)
            .Select(subscription => (JsonNode)subscription.ToJson());
        return Ok(new JsonObject { ["items"] = new JsonArray([.. items]), ["nextCursor"] = null });
    }

    private HttpResponseMessage ListPlans(int limit, string? cursor)
    {
        var start = cursor is null ? 0 : int.Parse(cursor, CultureInfo.InvariantCulture);
        var page = Plans.Values.OrderBy(plan => plan.Key, StringComparer.Ordinal).Skip(start).Take(limit).ToList();
        var next = start + page.Count < Plans.Count ? (start + page.Count).ToString(CultureInfo.InvariantCulture) : null;
        var items = page.Select(plan => (JsonNode)new JsonObject
        {
            ["id"] = Guid.CreateVersion7(),
            ["key"] = plan.Key,
            ["name"] = plan.Key,
            ["isArchived"] = plan.IsArchived,
            ["features"] = new JsonObject(),
            ["kind"] = plan.Kind,
        });
        return Ok(new JsonObject { ["items"] = new JsonArray([.. items]), ["nextCursor"] = next });
    }

    private HttpResponseMessage Create(JsonObject body, DateTimeOffset now)
    {
        var type = body["subscriberType"]!.GetValue<string>();
        var subscriberId = body["subscriberId"]!.GetValue<Guid>();
        if (type == "user" ? !Users.ContainsKey(subscriberId) : !Organizations.ContainsKey(subscriberId))
        {
            return Problem(HttpStatusCode.NotFound, "subscription.subscriber_not_found", "The user or organization does not exist.");
        }

        var key = body["plan"]!.GetValue<string>();
        if (!Plans.TryGetValue(key, out var plan))
        {
            return Problem(HttpStatusCode.NotFound, "plan.not_found", "The plan does not exist.");
        }

        if (plan.Kind != "base" || plan.IsArchived)
        {
            return Problem(HttpStatusCode.BadRequest, plan.IsArchived ? "plan.archived" : "plan.not_a_base_plan", "The plan takes no subscribers.");
        }

        if (_subscriptions.Any(subscription => subscription.SubscriberType == type && subscription.SubscriberId == subscriberId && subscription.Status != "expired"))
        {
            return Problem(HttpStatusCode.Conflict, "subscription.exists", "The subscriber already has a current subscription; change its plan instead.");
        }

        if (!TryReadTime(body, "trialEndsAt", out var trialEndsAt, out _) || !TryReadTime(body, "currentPeriodEnd", out var periodEnd, out _))
        {
            return Problem(HttpStatusCode.BadRequest, "request.invalid", "The request body is not valid.");
        }

        var created = new Subscription(Guid.CreateVersion7(), type, subscriberId, key, now);
        if (!created.Update(trialEndsAt, periodEnd, body["externalReference"]?.GetValue<string>(), trialEndsAt > now ? "trialing" : "active"))
        {
            return Invalid();
        }

        _subscriptions.Add(created);
        return Ok(created.ToJson(), HttpStatusCode.Created);
    }

    private HttpResponseMessage Update(Subscription subscription, JsonObject body)
    {
        if (!TryReadTime(body, "trialEndsAt", out var trialEndsAt, out var trialGiven) || !TryReadTime(body, "currentPeriodEnd", out var periodEnd, out var periodGiven))
        {
            return Problem(HttpStatusCode.BadRequest, "request.invalid", "The request body is not valid.");
        }

        if (body["plan"]?.GetValue<string>() is { } key)
        {
            if (!Plans.TryGetValue(key, out var plan))
            {
                return Problem(HttpStatusCode.NotFound, "plan.not_found", "The plan does not exist.");
            }

            if (key != subscription.Plan)
            {
                if (plan.IsArchived || subscription.Status == "expired")
                {
                    return Problem(HttpStatusCode.BadRequest, plan.IsArchived ? "plan.archived" : "subscription.ended", "The plan cannot be changed.");
                }

                subscription.Plan = key;
            }
        }

        var status = body["status"]?.GetValue<string>() ?? subscription.Status;
        if (subscription.Status == "expired")
        {
            return Problem(HttpStatusCode.BadRequest, "subscription.ended", "The subscription has ended or is already canceled.");
        }

        if (status is not ("trialing" or "active") && status != subscription.Status)
        {
            return Problem(HttpStatusCode.BadRequest, "subscription.invalid_status", "A subscription can be set to trialing or active; cancel it to end it.");
        }

        var updated = subscription.Update(
            trialGiven ? trialEndsAt : subscription.TrialEndsAt,
            periodGiven ? periodEnd : subscription.CurrentPeriodEnd,
            body.ContainsKey("externalReference") ? body["externalReference"]?.GetValue<string>() : subscription.ExternalReference,
            status);
        return updated ? Ok(subscription.ToJson()) : Invalid();
    }

    private static HttpResponseMessage Cancel(Subscription subscription, DateTimeOffset now)
    {
        if (subscription.Status is "expired" or "canceled")
        {
            return Problem(HttpStatusCode.BadRequest, "subscription.ended", "The subscription has ended or is already canceled.");
        }

        subscription.CanceledAt = now;
        subscription.Status = subscription.Status == "active" && subscription.CurrentPeriodEnd > now ? "canceled" : "expired";
        return Ok(subscription.ToJson());
    }

    public sealed record Recorded(string Method, string Path, string? Body)
    {
        public JsonObject? Json => Body is null ? null : JsonNode.Parse(Body)!.AsObject();
    }

    public sealed record User(Guid Id, KimlikUserStatus Status, IReadOnlyList<string> Roles);

    public sealed record Plan(string Key, string Kind, bool IsArchived);

    /// <summary>A Kimlik subscription with Kimlik's rules (Kimlik.Domain.Plans.Subscription).</summary>
    public sealed class Subscription(Guid id, string subscriberType, Guid subscriberId, string plan, DateTimeOffset created)
    {
        public Guid Id { get; } = id;

        public string SubscriberType { get; } = subscriberType;

        public Guid SubscriberId { get; } = subscriberId;

        public string Plan { get; set; } = plan;

        public string Status { get; set; } = "active";

        public DateTimeOffset Created { get; } = created;

        public DateTimeOffset CurrentPeriodStart { get; } = created;

        public DateTimeOffset? CurrentPeriodEnd { get; set; }

        public DateTimeOffset? TrialEndsAt { get; set; }

        public DateTimeOffset? CanceledAt { get; set; }

        public string? ExternalReference { get; set; }

        public DateTimeOffset? EndsAt => Status switch
        {
            "trialing" => TrialEndsAt,
            "active" or "canceled" => CurrentPeriodEnd,
            _ => null,
        };

        public bool IsInEffectAt(DateTimeOffset now) => Status != "expired" && (EndsAt is not { } end || now < end);

        public bool Update(DateTimeOffset? trialEndsAt, DateTimeOffset? currentPeriodEnd, string? externalReference, string status)
        {
            if ((status == "trialing" && trialEndsAt is null) || externalReference?.Trim().Length > 200 || currentPeriodEnd <= CurrentPeriodStart)
            {
                return false;
            }

            TrialEndsAt = trialEndsAt;
            CurrentPeriodEnd = currentPeriodEnd;
            ExternalReference = string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim();
            if (status != "canceled")
            {
                CanceledAt = null;
            }

            Status = status;
            return true;
        }

        public void ExpireIfEnded(DateTimeOffset now)
        {
            if (Status != "expired" && !IsInEffectAt(now))
            {
                Status = "expired";
            }
        }

        public JsonObject ToJson() => new()
        {
            ["id"] = Id,
            ["subscriberType"] = SubscriberType,
            ["subscriberId"] = SubscriberId,
            ["plan"] = Plan,
            ["status"] = Status,
            ["currentPeriodStart"] = CurrentPeriodStart,
            ["currentPeriodEnd"] = CurrentPeriodEnd,
            ["trialEndsAt"] = TrialEndsAt,
            ["canceledAt"] = CanceledAt,
            ["externalReference"] = ExternalReference,
            ["createdAt"] = Created,
            ["updatedAt"] = Created,
            ["addOns"] = new JsonObject(),
            ["featureOverrides"] = new JsonObject(),
        };
    }
}
