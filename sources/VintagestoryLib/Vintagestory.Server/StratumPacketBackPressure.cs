using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Vintagestory.Server;

// Bounded, fair drain of ClientPackets each main-thread tick.
// Vanilla drains the queue unboundedly, which can stall the tick for seconds when one
// client (or a packet flood) buries the main thread. This class:
//   - caps total wall-clock time spent dispatching per tick,
//   - caps packets dispatched per client per tick,
//   - requeues leftovers for the next tick (FIFO order is preserved per client),
//   - optionally kicks a client whose backlog grows past a hard limit.
internal sealed class StratumPacketBackPressure
{
	private readonly object gate = new object();
	private readonly Dictionary<int, int> servedThisTick = new Dictionary<int, int>();
	private readonly Dictionary<int, int> backlogPerClient = new Dictionary<int, int>();
	private readonly List<ReceivedClientPacket> deferred = new List<ReceivedClientPacket>(256);

	private long totalDispatched;
	private long totalDeferred;
	private long totalKicked;
	private long lastTickElapsedTicks;
	private int lastTickDispatched;
	private int lastTickDeferred;

	public (long Dispatched, long Deferred, long Kicked, double LastTickMs, int LastTickDispatched, int LastTickDeferred) Snapshot()
	{
		lock (gate)
		{
			double ms = lastTickElapsedTicks * 1000.0 / Stopwatch.Frequency;
			return (totalDispatched, totalDeferred, totalKicked, ms, lastTickDispatched, lastTickDeferred);
		}
	}

	public void ForgetClient(int clientId)
	{
		lock (gate)
		{
			servedThisTick.Remove(clientId);
			backlogPerClient.Remove(clientId);
		}
	}

	// Drains `source` and dispatches packets via `dispatch`. Returns the number of packets
	// actually dispatched this tick. Leftovers stay in `source` for the next tick. When a
	// client's backlog passes the configured kick threshold, `kick` is invoked once with
	// the offending client and a disconnect reason.
	public int DrainAndDispatch(
		ConcurrentQueue<ReceivedClientPacket> source,
		Action<ReceivedClientPacket> dispatch,
		Action<ConnectedClient, string> kick)
	{
		StratumConfig cfg = StratumRuntime.Config;
		cfg.EnsurePopulated();
		StratumPacketBackPressureConfig bp = cfg.PacketBackPressure;

		if (!bp.Enabled)
		{
			int n = 0;
			while (source.TryDequeue(out ReceivedClientPacket pkt))
			{
				dispatch(pkt);
				n++;
			}
			lock (gate)
			{
				totalDispatched += n;
				lastTickDispatched = n;
				lastTickDeferred = 0;
				lastTickElapsedTicks = 0;
			}
			return n;
		}

		int maxMs = Math.Max(1, bp.MaxMillisecondsPerTick);
		int perClientCap = Math.Max(1, bp.MaxPacketsPerClientPerTick);
		int queueKick = Math.Max(perClientCap * 4, bp.MaxQueueDepthPerClient);

		long start = Stopwatch.GetTimestamp();
		long ticksPerMs = Stopwatch.Frequency / 1000;
		long budgetTicks = (long)maxMs * ticksPerMs;

		servedThisTick.Clear();
		backlogPerClient.Clear();
		deferred.Clear();
		int dispatched = 0;
		HashSet<int> kicked = null;

		while (true)
		{
			if (Stopwatch.GetTimestamp() - start >= budgetTicks) break;
			if (!source.TryDequeue(out ReceivedClientPacket pkt)) break;

			int cid = pkt.client?.Id ?? -1;

			// Always let connect/disconnect packets through; they're rare and stateful.
			if (pkt.type != ReceivedClientPacketType.PacketReceived)
			{
				dispatch(pkt);
				dispatched++;
				continue;
			}

			if (cid >= 0 && kicked != null && kicked.Contains(cid))
			{
				// Already kicked this tick; drop further packets from this client.
				continue;
			}

			servedThisTick.TryGetValue(cid, out int served);
			if (served >= perClientCap)
			{
				deferred.Add(pkt);
				backlogPerClient.TryGetValue(cid, out int backlog);
				backlog++;
				backlogPerClient[cid] = backlog;

				if (bp.KickOnQueueOverflow && backlog > queueKick && pkt.client != null)
				{
					kicked ??= new HashSet<int>();
					if (kicked.Add(cid))
					{
						kick(pkt.client, string.IsNullOrWhiteSpace(bp.KickMessage)
							? "Disconnected by Stratum packet back-pressure"
							: bp.KickMessage);
						lock (gate) totalKicked++;
					}
				}
				continue;
			}

			dispatch(pkt);
			dispatched++;
			servedThisTick[cid] = served + 1;
		}

		// Requeue deferred packets (FIFO order preserved per client because we kept add order).
		int deferredCount = deferred.Count;
		for (int i = 0; i < deferredCount; i++)
		{
			source.Enqueue(deferred[i]);
		}
		deferred.Clear();

		long elapsed = Stopwatch.GetTimestamp() - start;
		lock (gate)
		{
			totalDispatched += dispatched;
			totalDeferred += deferredCount;
			lastTickDispatched = dispatched;
			lastTickDeferred = deferredCount;
			lastTickElapsedTicks = elapsed;
		}
		return dispatched;
	}

	public string BuildReport()
	{
		StratumPacketBackPressureConfig bp = StratumRuntime.Config.PacketBackPressure;
		var s = Snapshot();
		StringBuilder sb = new StringBuilder();
		sb.AppendLine($"Packet back-pressure: enabled={bp.Enabled}, budget={bp.MaxMillisecondsPerTick}ms/tick, perClientCap={bp.MaxPacketsPerClientPerTick}/tick, queueKickAt={bp.MaxQueueDepthPerClient}");
		sb.Append($"Totals: dispatched={s.Dispatched}, deferred={s.Deferred}, kicked={s.Kicked}. Last tick: {s.LastTickMs:F2}ms, dispatched={s.LastTickDispatched}, deferred={s.LastTickDeferred}");
		return sb.ToString();
	}
}
