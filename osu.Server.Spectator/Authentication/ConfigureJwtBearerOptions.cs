// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using osu.Server.Spectator.Database;

namespace osu.Server.Spectator.Authentication
{
    public class ConfigureJwtBearerOptions : IConfigureNamedOptions<JwtBearerOptions>
    {
        public const string LAZER_CLIENT_SCHEME = "lazer";
        public const string REFEREE_CLIENT_SCHEME = "referee";

        private readonly IDatabaseFactory databaseFactory;
        private readonly ILoggerFactory loggerFactory;

        public ConfigureJwtBearerOptions(IDatabaseFactory databaseFactory, ILoggerFactory loggerFactory)
        {
            this.databaseFactory = databaseFactory;
            this.loggerFactory = loggerFactory;
        }

        // this looks very scary, but ASP.NET never calls this and calls the named variant instead. don't ask why.
        public void Configure(JwtBearerOptions options)
            => throw new NotSupportedException();

        public void Configure(string? name, JwtBearerOptions options)
        {
            switch (name)
            {
                case LAZER_CLIENT_SCHEME:
                    configureLazerClientScheme(options);
                    return;

                case REFEREE_CLIENT_SCHEME:
                    configureRefereeClientScheme(options);
                    return;
            }
        }

        private void configureLazerClientScheme(JwtBearerOptions options)
        {
            // Torii: g0v0 issues HS256-signed JWTs (it doesn't hold osu!web's RSA private key).
            // Operators that point this spectator at upstream osu!web instead can flip
            // USE_LEGACY_RSA_AUTH=true to fall back to the public-key validation path.
            SecurityKey signingKey;

            if (AppSettings.UseLegacyRsaAuth)
            {
                signingKey = new RsaSecurityKey(getKeyProvider());
            }
            else
            {
                if (string.IsNullOrEmpty(AppSettings.JwtSecretKey) || AppSettings.JwtSecretKey == "your_jwt_secret_here")
                {
                    throw new InvalidOperationException(
                        "JWT_SECRET_KEY is required when USE_LEGACY_RSA_AUTH is false. "
                        + "Set it to the same value g0v0 uses to sign access tokens.");
                }

                signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AppSettings.JwtSecretKey));
            }

            options.TokenValidationParameters = new TokenValidationParameters
            {
                IssuerSigningKey = signingKey,
                ValidateIssuerSigningKey = true,
                ValidAudience = AppSettings.OsuClientId.ToString(),
                ValidateAudience = true,
                // TODO: figure out why this isn't included in the token.
                ValidateIssuer = false,
                ValidIssuer = "https://osu.ppy.sh/",
                ValidateLifetime = true,
                // Torii: g0v0 doesn't run NTP-synced precisely with the spectator host, and
                // tokens are short-lived. 5 minutes of clock skew avoids "token not yet valid"
                // / "expired by 1s" rejections without meaningfully widening the auth window.
                ClockSkew = TimeSpan.FromMinutes(5),
                RequireExpirationTime = true,
            };

            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    var jwtToken = (JsonWebToken)context.SecurityToken;

                    if (!int.TryParse(jwtToken.Subject, out int tokenUserId))
                    {
                        context.Fail("Invalid token format");
                        return;
                    }

                    using var db = databaseFactory.GetInstance();

                    // Resolve the access_token row in the DB. Returns the CURRENT user_id
                    // (oauth_tokens.user_id), which can diverge from the JWT's sub claim if
                    // the user was migrated between IDs (account merge / id transfer).
                    var resolvedUserId = await db.GetUserIdFromTokenAsync(jwtToken);

                    if (resolvedUserId == null)
                    {
                        // Token row is gone (revoked or expired) — this is a real auth failure.
                        loggerFactory.CreateLogger("JsonWebToken").LogInformation("Token revoked or expired");
                        context.Fail("Token has expired or been revoked");
                        return;
                    }

                    if (resolvedUserId != tokenUserId)
                    {
                        // The user was migrated to a new id. Trust the DB and rebuild the
                        // ClaimsPrincipal so every downstream reader of Context.UserIdentifier
                        // / Identity.Name picks up the new id. Mutating claims in-place isn't
                        // enough because the JWT handler computes Identity.Name lazily and
                        // may have cached a value derived from the original sub.
                        if (context.Principal?.Identity is ClaimsIdentity oldIdentity)
                        {
                            string newId = resolvedUserId.Value.ToString();
                            var keep = oldIdentity.Claims
                                                  .Where(c =>
                                                      c.Type != "sub" &&
                                                      c.Type != ClaimTypes.NameIdentifier &&
                                                      c.Type != ClaimTypes.Name)
                                                  .ToList();
                            keep.Add(new Claim("sub", newId));
                            keep.Add(new Claim(ClaimTypes.NameIdentifier, newId));
                            keep.Add(new Claim(ClaimTypes.Name, newId));
                            var newIdentity = new ClaimsIdentity(
                                keep,
                                oldIdentity.AuthenticationType,
                                nameType: ClaimTypes.NameIdentifier,
                                roleType: oldIdentity.RoleClaimType);
                            context.Principal = new ClaimsPrincipal(newIdentity);
                        }

                        tokenUserId = resolvedUserId.Value;
                    }

                    // Restriction check happens here (after the principal is correct) so
                    // restricted users can't slip through by clinging to a pre-migration id.
                    if (await db.IsUserRestrictedAsync(tokenUserId))
                    {
                        context.Fail("User account is restricted");
                    }
                },
            };
        }

        private void configureRefereeClientScheme(JwtBearerOptions options)
        {
            var rsa = getKeyProvider();

            options.TokenValidationParameters = new TokenValidationParameters
            {
                IssuerSigningKey = new RsaSecurityKey(rsa),
                // there could be multiple valid audiences here, so we're not checking.
                // the access to the referee API is controlled by possessing the `multiplayer.write_manage` scope instead,
                // and that scope is checked for at endpoint access time via `[Authorize]` attributes rather than at JWT validation time.
                ValidateAudience = false,
                // TODO: figure out why this isn't included in the token.
                ValidateIssuer = false,
                ValidIssuer = "https://osu.ppy.sh/"
            };

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // see https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0#built-in-jwt-authentication
                    var accessToken = context.Request.Query["access_token"];
                    if (!string.IsNullOrEmpty(accessToken))
                        context.Token = accessToken;
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    var jwtToken = (JsonWebToken)context.SecurityToken;

                    using var db = databaseFactory.GetInstance();

                    if (int.TryParse(jwtToken.Subject, out int tokenUserId))
                    {
                        // token has non-empty subject => issued via authorization code flow
                        // check expiry/revocation against database
                        var userId = await db.GetUserIdFromTokenAsync(jwtToken);

                        if (userId != tokenUserId)
                        {
                            loggerFactory.CreateLogger("JsonWebToken").LogInformation("Token revoked or expired");
                            context.Fail("Token has expired or been revoked");
                        }
                    }
                    else
                    {
                        // no subject => token issued via client_credentials flow with delegation
                        // check expiry/revocation against database
                        var resourceOwnerId = await db.GetDelegatedResourceOwnerIdFromTokenAsync(jwtToken);

                        if (resourceOwnerId == null)
                        {
                            loggerFactory.CreateLogger("JsonWebToken").LogInformation("Token revoked or expired");
                            context.Fail("Token has expired or been revoked");
                            return;
                        }

                        // the token is issued with no user associated with it.
                        // however we've checked above that the token has been issued with the `delegation` scope
                        // which means it is permissible to delegate actions from this client to its owner.
                        // therefore, append a relevant claim manually so that we can use it later to identify the user.
                        var identity = new ClaimsIdentity();
                        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, resourceOwnerId.Value.ToString()));
                        context.Principal!.AddIdentity(identity);
                    }
                },
            };
        }

        /// <summary>
        /// borrowed from https://stackoverflow.com/a/54323524
        /// </summary>
        private static RSACryptoServiceProvider getKeyProvider()
        {
            string key = File.ReadAllText("oauth-public.key");

            key = key.Replace("-----BEGIN PUBLIC KEY-----", "");
            key = key.Replace("-----END PUBLIC KEY-----", "");
            key = key.Replace("\n", "");

            var keyBytes = Convert.FromBase64String(key);

            var asymmetricKeyParameter = PublicKeyFactory.CreateKey(keyBytes);
            var rsaKeyParameters = (RsaKeyParameters)asymmetricKeyParameter;
            var rsaParameters = new RSAParameters
            {
                Modulus = rsaKeyParameters.Modulus.ToByteArrayUnsigned(),
                Exponent = rsaKeyParameters.Exponent.ToByteArrayUnsigned()
            };

            var rsa = new RSACryptoServiceProvider();
            rsa.ImportParameters(rsaParameters);

            return rsa;
        }
    }
}
