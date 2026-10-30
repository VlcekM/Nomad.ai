using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Nomad.Hub.Contracts;
using Nomad.Hub.Data;

namespace Nomad.Hub.Services;

/// <summary>
/// Each Nomad's one live connection: presence, lease renewals and "the change feed moved" pings
/// (<see cref="HubNames.Changed"/>), and handoff requests to the PC holding a session.
/// </summary>
[Authorize]
public sealed class NomadHub(HubDb db, ChangeLog changes, Presence presence) : Microsoft.AspNetCore.SignalR.Hub
{
    public static string DeviceGroup(Guid device) => $"device:{device:N}";

    public override async Task OnConnectedAsync()
    {
        var device = Context.User!.DeviceId();
        presence.Connected(device);
        await Groups.AddToGroupAsync(Context.ConnectionId, DeviceGroup(device));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Disconnected(Context.User!.DeviceId());
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Renews the caller's leases; returns those another PC holds now.</summary>
    public Task<List<LeaseKey>> RenewLeases(List<LeaseKey> keys) => Leases.RenewAsync(db, changes, Context.User!.DeviceId(), keys);
}

public static class Leases
{
    public static async Task<List<LeaseKey>> RenewAsync(HubDb db, ChangeLog changes, Guid device, IReadOnlyList<LeaseKey> keys)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        return await changes.WriteAsync(db, async nextSeq =>
        {
            var lost = new List<LeaseKey>();
            var now = DateTime.UtcNow;
            foreach (var key in keys)
            {
                var session = await Sessions.FindOrCreateAsync(db, key.Backend, key.SessionId);
                if (session.LeaseDevice is { } holder && holder != device && session.LeaseExpiresUtc > now)
                {
                    lost.Add(key);
                    continue;
                }

                if (session.LeaseDevice != device)
                {
                    session.Seq = await nextSeq();
                }

                session.LeaseDevice = device;
                session.LeaseExpiresUtc = now + HubNames.LeaseLifetime;
            }

            return lost;
        });
    }
}
