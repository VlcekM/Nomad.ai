using Nomad.Core.Protocol;

namespace Nomad.Core.Agents;

/// <summary>
/// An agent that takes a prompt into the turn it is running, without stopping it: Claude Code reads it at its next step, Codex
/// adds it to the turn (<c>turn/steer</c>), Antigravity carries on its work with it.
/// </summary>
public interface ISteerableBackend
{
    /// <summary>A prompt can go into the running turn now.</summary>
    bool CanSteer { get; }

    /// <summary>
    /// Sends a prompt into the running turn. <see cref="PromptTakenEvent"/> names <paramref name="uuid"/> once the agent has
    /// it. Throws when it could not go in (the turn ended just then): the caller queues it instead.
    /// </summary>
    Task SteerAsync(string text, IReadOnlyList<PromptImage>? images, string uuid, CancellationToken cancellationToken = default);
}
