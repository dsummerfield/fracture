﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Squared.Util;

namespace Squared.Game.Animation {
    public interface AnimCmd {
        bool Invoke (Animator animator);
    }

    public class SetAnimation : AnimCmd {
        public IEnumerator<AnimCmd> Animation;

        public bool Invoke (Animator animator) {
            animator.SetAnimation(Animation);
            return false;
        }
    }

    public class SetFrame : AnimCmd {
        public int Group;
        public int Frame;

        public bool Invoke (Animator animator) {
            animator.SetFrame(Group, Frame);
            return false;
        }
    }

    public class Delay : AnimCmd {
        public long Duration;

        public bool Invoke (Animator animator) {
            animator.Delay(Duration);
            return false;
        }
    }

    public class WaitForUpdate : AnimCmd {
        public bool Invoke (Animator animator) {
            return true;
        }
    }

    public static class AnimationExtensions {
        public static IEnumerator<AnimCmd> Chain (this IEnumerator<AnimCmd> first, Func<IEnumerator<AnimCmd>> second) {
            using (first)
                while (first.MoveNext())
                    yield return first.Current;

            yield return new SetAnimation { Animation = second() };
        }

        public static IEnumerator<AnimCmd> SwitchIf (this IEnumerator<AnimCmd> root, Func<IEnumerator<AnimCmd>> leaf, Func<bool> predicate) {
            using (root) {
                while (root.MoveNext()) {
                    if (predicate()) {
                        yield return new SetAnimation { Animation = leaf() };
                        break;
                    } else {
                        yield return root.Current;
                    }
                }
            }
        }
    }

    public class Animator {
        public ITimeProvider TimeProvider = Time.DefaultTimeProvider;
        private IEnumerator<AnimCmd> _ActiveAnimation = null;
        private int _Group = 0, _Frame = 0;
        private long _SuspendUntil = 0;

        public void SetAnimation (IEnumerator<AnimCmd> animation) {
            if (_ActiveAnimation != null)
                _ActiveAnimation.Dispose();

            _ActiveAnimation = animation;
            _SuspendUntil = TimeProvider.Ticks;
        }

        public void SetFrame (int group, int frame) {
            _Group = group;
            _Frame = frame;
        }

        public void Delay (long duration) {
            _SuspendUntil = _SuspendUntil + duration;
        }

        public int Group {
            get { return _Group; }
        }

        public int Frame {
            get { return _Frame; }
        }

        public void Update () {
            long now = TimeProvider.Ticks;
            while ((_ActiveAnimation != null) && (now >= _SuspendUntil)) {
                if (!_ActiveAnimation.MoveNext()) {
                    _ActiveAnimation = null;
                    break;
                }

                var item = _ActiveAnimation.Current;
                if (item.Invoke(this))
                    break;
            }
        }
    }
}
