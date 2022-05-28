﻿using FishNet.Managing;
using FishNet.Managing.Logging;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace RVP
{
    /// <summary>
    /// This can probably be replaced with the normal one now (once it has had the Update fix) - I thought i was doing something i wasn't!
    /// </summary>
    public partial class PredictedObjectCache : NetworkBehaviour
    {
        /// <summary>
        /// How often to synchronize values from server to clients when no changes have been detected.
        /// </summary>
        protected const float SEND_INTERVAL = 1f;

        /// <summary>
        /// Transform which holds the graphical features of this object. This transform will be smoothed when desynchronizations occur.
        /// </summary>
        [Tooltip("Transform which holds the graphical features of this object. This transform will be smoothed when desynchronizations occur.")]
        [SerializeField]
        private Transform _graphicalObject;

        /// <summary>
        /// Rigidbody to predict.
        /// </summary>
        [Tooltip("Rigidbody to predict.")]
        [SerializeField]
        private Rigidbody _rigidbody;

        [Tooltip("How much to damp any desync.")]
        [Range(0.001f, 1f)]
        public float smoothDamping = 0.05f;

        /// <summary>
        /// True if subscribed to events.
        /// </summary>
        private bool _subscribed;

        /// <summary>
        /// World position before transform was predicted or reset.
        /// </summary>
        private Vector3 _previousPosition;

        /// <summary>
        /// World rotation before transform was predicted or reset.
        /// </summary>
        private Quaternion _previousRotation;

        /// <summary>
        /// Local position of transform when instantiated.
        /// </summary>
        private Vector3 _instantiatedLocalPosition;

        /// <summary>
        /// Local rotation of transform when instantiated.
        /// </summary>
        private Quaternion _instantiatedLocalRotation;

        /// <summary>
        /// Last sent state
        /// </summary>
        private RigidbodyState? _cachedRigidbodyState;

        public override void OnStartNetwork()
        {
            base.OnStartNetwork();
            base.TimeManager.OnPostTick += TimeManager_OnPostTick;
            _instantiatedLocalPosition = _graphicalObject.localPosition;
            _instantiatedLocalRotation = _graphicalObject.localRotation;
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            ChangeSubscriptions(true);
        }

        public override void OnStopClient()
        {
            base.OnStopClient();

            ChangeSubscriptions(false);
        }

        public override void OnStopNetwork()
        {
            base.OnStopNetwork();

            if (base.TimeManager != null)
            {
                base.TimeManager.OnPostTick -= TimeManager_OnPostTick;
            }
        }

        protected void TimeManager_OnPostTick()
        {
            if (base.IsClient)
            {
                ResetToTransformPreviousProperties();
            }

            // another bodge - there are going to multiple vehicles - this assumes the reconcilation rate is the same on all
            if (base.IsServer && PredictedVehicle.ReconcilationGlobalTickStep != 0)
            {
                uint localTick = base.TimeManager.LocalTick;

                // ok this is a bit greed - its sending its position regrdless of its moving or not
                if ((localTick % PredictedVehicle.ReconcilationGlobalTickStep) == 0)
                {
                    SendRigidbodyState();
                }
            }
        }

        /// <summary>
        /// Called before performing a reconcile on NetworkBehaviour.
        /// </summary>
        protected virtual void TimeManager_OnPreReconcile(NetworkBehaviour obj)
        {
            SetPreviousTransformProperties();

            if (_cachedRigidbodyState.HasValue)
            {
                _rigidbody.transform.position = _cachedRigidbodyState.Value.Position;
                _rigidbody.transform.rotation = _cachedRigidbodyState.Value.Rotation;
                _rigidbody.velocity = _cachedRigidbodyState.Value.Velocity;
                _rigidbody.angularVelocity = _cachedRigidbodyState.Value.AngularVelocity;
            }
        }

        private void TimeManager_OnPostReconcile(NetworkBehaviour obj)
        {
            ResetToTransformPreviousProperties();
        }

        /// <summary>
        /// Returns if this transform matches arguments.
        /// </summary>
        /// <returns></returns>
        protected bool GraphicalObjectMatches(Vector3 position, Quaternion rotation)
        {
            return (_graphicalObject.localPosition == position && _graphicalObject.localRotation == rotation);
        }

        private void Awake()
        {
            //Set in awake so they arent default.
            SetPreviousTransformProperties();

            if (Application.isPlaying)
                InitializeOnce();
        }

        private void Update()
        {
            MoveToTarget();
        }

        /// <summary>
        /// Sends current states of this object to client.
        /// </summary>
        private void SendRigidbodyState()
        {
            RigidbodyState state = new RigidbodyState
            {
                Position = _rigidbody.transform.position,
                Rotation = _rigidbody.transform.rotation,
                Velocity = _rigidbody.velocity,
                AngularVelocity = _rigidbody.angularVelocity
            };

            ObserversSendRigidbodyState(state);
        }

        /// <summary>
        /// Sends transform and rigidbody state to spectators.
        /// </summary>
        /// <param name="state"></param>
        [ObserversRpc(IncludeOwner = false, BufferLast = true)]
        private void ObserversSendRigidbodyState(RigidbodyState state, Channel channel = Channel.Unreliable)
        {
            if (!base.IsOwner && !base.IsServer)
            {
                // we just place in cache as this data is already old regards the client tick
                // so when the client reconcilates we will use this cache to fix up rb position

                // store state in cache slot
                _cachedRigidbodyState = state;
            }
        }

        private void TimeManager_OnPreTick()
        {
            SetPreviousTransformProperties();
        }

        /// <summary>
        /// Subscribes to events needed to function.
        /// </summary>
        /// <param name="subscribe"></param>
        private void ChangeSubscriptions(bool subscribe)
        {
            if (base.TimeManager == null)
                return;
            if (subscribe == _subscribed)
                return;

            if (subscribe)
            {
                base.TimeManager.OnPreTick += TimeManager_OnPreTick;
                base.TimeManager.OnPreReconcile += TimeManager_OnPreReconcile;
                base.TimeManager.OnPostReconcile += TimeManager_OnPostReconcile;
            }
            else
            {
                base.TimeManager.OnPreTick -= TimeManager_OnPreTick;
                base.TimeManager.OnPreReconcile -= TimeManager_OnPreReconcile;
                base.TimeManager.OnPostReconcile -= TimeManager_OnPostReconcile;
            }

            _subscribed = subscribe;
        }

        /// <summary>
        /// Initializes this script for use.
        /// </summary>
        private void InitializeOnce()
        {
            //No graphical object, cannot smooth.
            if (_graphicalObject == null)
            {
                if (NetworkManager.StaticCanLog(LoggingType.Error))
                    Debug.LogError($"GraphicalObject is not set on {gameObject.name}. Initialization will fail.");
                return;
            }
        }

        /// <summary>
        /// Moves transform to target values.
        /// </summary>
        private void MoveToTarget()
        {
            Transform t = _graphicalObject.transform;
            float delta = Time.deltaTime;
            t.localPosition = Vector3.Lerp(t.localPosition, _instantiatedLocalPosition, delta / smoothDamping);
            t.localRotation = Quaternion.Slerp(t.localRotation, _instantiatedLocalRotation, delta / smoothDamping);
        }

        /// <summary>
        /// Caches the transforms current position and rotation.
        /// </summary>
        private void SetPreviousTransformProperties()
        {
            _previousPosition = _graphicalObject.position;
            _previousRotation = _graphicalObject.rotation;
        }

        /// <summary>
        /// Resets the transform to cached position and rotation of the transform.
        /// </summary>
        private void ResetToTransformPreviousProperties()
        {
            _graphicalObject.position = _previousPosition;
            _graphicalObject.rotation = _previousRotation;
        }

        public struct RigidbodyState
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
            public Vector3 AngularVelocity;
        }
    }
}