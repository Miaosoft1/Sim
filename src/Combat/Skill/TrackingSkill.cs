using Miaosoft.Sim.Combat.Map;
using Miaosoft.Sim.Combat.Map.EventData;
using Miaosoft.Sim.Combat.Map.Visibility;

namespace Miaosoft.Sim.Combat.Skill
{
    public sealed class TrackingSkill : IScopedMapVisibilityRule
    {
        private readonly object gate = new();
        private readonly Dictionary<TrackKey, int> activeTracks = [];

        public IDisposable Apply(MapObject caster, MapObject target)
        {
            ArgumentNullException.ThrowIfNull(caster);
            ArgumentNullException.ThrowIfNull(target);
            if (caster.Kind != MapObjectKind.Player)
            {
                throw new ArgumentException("A tracking skill caster must be a player.", nameof(caster));
            }

            if (target.Kind == MapObjectKind.Dungeon)
            {
                throw new ArgumentException("A tracking skill can only target a game unit.", nameof(target));
            }

            TrackKey key = new(caster.Id, target.Id);
            lock (gate)
            {
                activeTracks.TryGetValue(key, out int count);
                activeTracks[key] = count + 1;
            }

            IDisposable? ruleLease = caster.Manager?.RegisterVisibilityRule(this) ??
                caster.MapRuntime?.RegisterVisibilityRule(this);
            NotifyChanged(caster);
            return new TrackingEffect(this, key, caster, ruleLease);
        }

        public bool? GetVisibilityOverride(MapObject viewer, MapObject target)
        {
            ArgumentNullException.ThrowIfNull(viewer);
            ArgumentNullException.ThrowIfNull(target);
            lock (gate)
            {
                return activeTracks.ContainsKey(new TrackKey(viewer.Id, target.Id)) ? true : null;
            }
        }

        public IReadOnlyCollection<(string ViewerId, string TargetId)> GetAffectedPairs()
        {
            lock (gate)
            {
                return activeTracks.Keys.Select(key => (key.CasterId, key.TargetId)).ToArray();
            }
        }

        private void RemoveTrack(TrackKey key)
        {
            lock (gate)
            {
                if (!activeTracks.TryGetValue(key, out int count))
                {
                    return;
                }

                if (count == 1)
                {
                    activeTracks.Remove(key);
                }
                else
                {
                    activeTracks[key] = count - 1;
                }
            }
        }

        private readonly record struct TrackKey(string CasterId, string TargetId);

        private static void NotifyChanged(MapObject caster)
        {
            Context? context = caster.EventReceiver.Context;
            if (context == null) return;
            context.Dispatch(context.Factory.NewEvent<MapVisibilityChangedEvent>() with
            {
                Receiver = caster.EventReceiver, TimeSpan = TimeSpan.Zero
            }).ToList();
        }

        private sealed class TrackingEffect : IDisposable
        {
            private TrackingSkill? skill;
            private readonly TrackKey key;
            private readonly MapObject caster;
            private readonly IDisposable? ruleLease;

            public TrackingEffect(TrackingSkill skill, TrackKey key, MapObject caster, IDisposable? ruleLease)
            {
                this.skill = skill;
                this.key = key;
                this.caster = caster;
                this.ruleLease = ruleLease;
            }

            public void Dispose()
            {
                TrackingSkill? current = Interlocked.Exchange(ref skill, null);
                if (current == null) return;
                current.RemoveTrack(key);
                ruleLease?.Dispose();
                NotifyChanged(caster);
            }
        }
    }
}
