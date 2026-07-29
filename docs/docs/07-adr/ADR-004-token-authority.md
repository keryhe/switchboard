# ADR-004: Self-Issued JWT (vs. External Identity Provider)

**Status:** Accepted  
**Date:** 2025-10

---

## Context

Clients authenticate to the service's WebSocket endpoint using a short-lived JWT issued during negotiation. The question is who issues this token and how it is signed.

Options:
1. **Self-issued JWT** — the service generates and signs tokens using a configured HMAC or RSA key
2. **External identity provider** — tokens are issued by an IdP (Keycloak, Active Directory FS, Auth0 self-hosted) and validated by the service

---

## Decision

The service **self-issues short-lived JWTs** using HMAC-SHA256 with a configurable secret.

---

## Rationale

**Narrow purpose.** The client JWT has a single purpose: prove that a specific `connectionId` was legitimately issued for a specific `hubName` during a specific negotiation request. It is not a general-purpose identity token. A 60-second TTL means it expires before the WebSocket upgrade completes; it does not represent ongoing authorization.

**The app server is the trust boundary for user identity.** The app server authenticates users (via cookies, OAuth tokens, API keys — whatever the application uses). It is the app server's responsibility to decide whether a user may connect. The service trusts the app server's negotiation request. The JWT is the service's way of binding the connection ID to the WebSocket upgrade, not of asserting user identity.

**Zero operational dependency.** An external IdP for client tokens means the IdP becomes a critical path for every new client connection. Self-issued tokens require only the configured key.

**Simplicity.** `System.IdentityModel.Tokens.Jwt` is a single NuGet package. Issuing a token is a handful of lines. No discovery endpoints, no token introspection calls, no JWKS rotation ceremony.

**Short TTL limits exposure.** Even if a token is intercepted, it expires in 60 seconds. The `connectionId` embedded in the token is single-use; once the WebSocket upgrade completes, the token is no longer usable.

---

## Key Design Points

- **HMAC-SHA256 (single node):** A single symmetric key configured in `appsettings.json` or via environment variable. Simple; key must be the same on all nodes if multiple service nodes share a load balancer in front.
- **RSA (multi-node):** Use an RSA key pair. Each node signs with the private key. All nodes validate with the public key. Allows key rotation without a simultaneous restart of all nodes.
- **Key rotation:** Support listing multiple valid validation keys simultaneously (standard `TokenValidationParameters.IssuerSigningKeys`). Rotate by adding the new key, restarting instances one by one, then removing the old key after all instances have restarted.

---

## Consequences

- The signing key is a required configuration value. A missing key should cause the service to fail on startup (not silently issue invalid tokens).
- The management API uses a separate long-lived token (different issuer/audience). Do not reuse client token configuration for management auth.
- App server tokens (used by the connector library to authenticate server connections) use yet another secret. **Three token types, three independent signing keys:**

| Token | Signing key | Audience | Lifetime | Specified in |
|---|---|---|---|---|
| Client | `TokenSigningKey` | `TokenAudience` (`switchboard-client`) | ~60s | [04-design.md §1](../04-design.md), [03-protocol.md §1.1](../03-protocol.md#11-negotiation) |
| App server | `ServerSigningKey` | `switchboard-server` | ~24h | [03-protocol.md §2.1](../03-protocol.md#21-app-server-connection-establishment) |
| Management | `ManagementSigningKey` | `ManagementAudience` (`switchboard-management`) | ~24h | [03-protocol.md Part 3](../03-protocol.md#part-3-management-rest-api) |

  No key may be reused across rows — in particular, an app server token must never be able to drive the management API. Each supports a `…Fallback` key for zero-downtime rotation. The consolidated operations guide covering key generation, rotation, and storage — the Phase 4 deliverable this note originally pointed at as forthcoming — is now real: [docs/docs/10-operations.md](../10-operations.md) ([06-project-plan.md](../06-project-plan.md)).

---

## Alternatives Considered

**External IdP (Keycloak, ADFS)**  
Would unify identity across the platform. Rejected for client tokens because: (1) the token is not about user identity — it's a connection binding; (2) adds IdP as a critical path dependency; (3) token TTL management (short-lived tokens need frequent refresh from IdP).

**No token (connection ID in URL only)**  
Simpler. Rejected: without a signed token, any client that knows a connectionId can hijack the WebSocket upgrade. The JWT cryptographically binds the connectionId to the negotiation that produced it.

**Mutual TLS (mTLS)**  
Strong authentication. Rejected for client connections: requires client certificate management, which is impractical for browser-based SignalR clients.
