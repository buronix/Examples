using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Pathfinding;
using BEngine;

namespace FNetwork.Server
{
    [RequireComponent(typeof(Seeker))]
    public class NetworkCreatureController : MonoBehaviour
    {

        public GenericCreature Creature { private set; get; }

        /** Determines how often it will search for new paths.
	     * If you have fast moving targets or AIs, you might want to set it to a lower value.
	     * The value is in seconds between path requests.
	     */
        public float repathRate = 0.5F;

        /** Target to move towards.
         * The AI will try to follow/move towards this target.
         * It can be a point on the ground where the player has clicked in an RTS for example, or it can be the player object in a zombie game.
         */
        public Transform target;

        /** Enables or disables searching for paths.
         * Setting this to false does not stop any active path requests from being calculated or stop it from continuing to follow the current path.
         * \see #canMove
         */
        public bool canSearch = true;

        /** Enables or disables movement.
          * \see #canSearch */
        public bool canMove = true;

        /** Maximum velocity.
         * This is the maximum speed in world units per second.
         */
        public float speed = 5;

        /** Rotation speed.
         * Rotation is calculated using Quaternion.SLerp. This variable represents the damping, the higher, the faster it will be able to rotate.
         */
        public float turningSpeed = 10;

        /** Determines within what range it will switch to target the next waypoint in the path */
        public float pickNextWaypointDist = 4;

        /** Distance to the end point to consider the end of path to be reached.
         * When this has been reached, the AI will not move anymore until the target changes and OnTargetReached will be called.
         */
        public float endReachedDistance = 2F;

        /** Cached Seeker component */
        protected Seeker seeker;

        /** Cached Transform component */
        protected Transform tr;

        /** Time when the last path request was sent */
        protected float lastRepath = -9999;

        /** Current path which is followed */
        protected Path path;

        /** Cached CharacterController component */
        protected CharacterController controller;
        /** Current index in the path which is current target */
        protected int currentWaypointIndex = 0;

        /** Holds if the end-of-path is reached
         * \see TargetReached */
        protected bool targetReached = false;

        /** Only when the previous path has been returned should be search for a new path */
        protected bool canSearchAgain = true;

        protected Vector3 lastFoundWaypointPosition;
        protected float lastFoundWaypointTime = -9999;
        /** Point to where the AI is heading.
        * Filled in by #CalculateVelocity */
        protected Vector3 targetPoint;
        /** Relative direction to where the AI is heading.
         * Filled in by #CalculateVelocity */
        protected Vector3 targetDirection;

        public float gravity = 9.82F;

        /** Returns if the end-of-path has been reached
         * \see targetReached */
        public bool TargetReached
        {
            get
            {
                return targetReached;
            }
        }

        /** Holds if the Start function has been run.
         * Used to test if coroutines should be started in OnEnable to prevent calculating paths
         * in the awake stage (or rather before start on frame 0).
         */
        private bool startHasRun = false;
        /** Initializes reference variables.
         * If you override this function you should in most cases call base.Awake () at the start of it.
        * */
        protected virtual void Awake()
        {
            seeker = GetComponent<Seeker>();
            tr = transform;
            controller = GetComponent<CharacterController>();
        }

        // Use this for initialization
        void Start()
        {
            startHasRun = true;
            OnEnable();
        }

        public void setCreature(GenericCreature creature)
        {
            Creature = creature;
        }

        public virtual void OnTargetReached()
        {
            //End of path has been reached
            //If you want custom logic for when the AI has reached it's destination
            //add it here
            //You can also create a new script which inherits from this one
            //and override the function in that script
        }

        /** Run at start and when reenabled.
         * Starts RepeatTrySearchPath.
         *
         * \see Start
         */
        protected virtual void OnEnable()
        {

            lastRepath = -9999;
            canSearchAgain = true;

            lastFoundWaypointPosition = GetFeetPosition();

            if (startHasRun)
            {
                //Make sure we receive callbacks when paths complete
                seeker.pathCallback += OnPathComplete;

                StartCoroutine(RepeatTrySearchPath());
            }
        }
        public void OnDisable()
        {
            // Abort calculation of path
            if (seeker != null && !seeker.IsDone()) seeker.GetCurrentPath().Error();

            // Release current path
            if (path != null) path.Release(this);
            path = null;

            //Make sure we receive callbacks when paths complete
            seeker.pathCallback -= OnPathComplete;
        }

        /** Tries to search for a path every #repathRate seconds.
         * \see TrySearchPath
         */
        protected IEnumerator RepeatTrySearchPath()
        {
            while (true)
            {
                float v = TrySearchPath();
                yield return new WaitForSeconds(v);
            }
        }


        /** Tries to search for a path.
         * Will search for a new path if there was a sufficient time since the last repath and both
         * #canSearchAgain and #canSearch are true and there is a target.
         *
         * \returns The time to wait until calling this function again (based on #repathRate)
         */
        public float TrySearchPath()
        {
            if (Time.time - lastRepath >= repathRate && canSearchAgain && canSearch && target != null)
            {
                SearchPath();
                return repathRate;
            }
            else {
                //StartCoroutine (WaitForRepath ());
                float v = repathRate - (Time.time - lastRepath);
                return v < 0 ? 0 : v;
            }
        }

        /** Requests a path to the target */
        public virtual void SearchPath()
        {

            if (target == null) throw new System.InvalidOperationException("Target is null");

            lastRepath = Time.time;
            //This is where we should search to
            Vector3 targetPosition = target.position;

            canSearchAgain = false;

            //Alternative way of requesting the path
            //ABPath p = ABPath.Construct (GetFeetPosition(),targetPoint,null);
            //seeker.StartPath (p);

            //We should search from the current position
            seeker.StartPath(GetFeetPosition(), targetPosition);
        }

        /** Called when a requested path has finished calculation.
	  * A path is first requested by #SearchPath, it is then calculated, probably in the same or the next frame.
	  * Finally it is returned to the seeker which forwards it to this function.\n
	  */
        public virtual void OnPathComplete(Path _p)
        {
            ABPath p = _p as ABPath;
            if (p == null) throw new System.Exception("This function only handles ABPaths, do not use special path types");

            canSearchAgain = true;

            //Claim the new path
            p.Claim(this);

            // Path couldn't be calculated of some reason.
            // More info in p.errorLog (debug string)
            if (p.error)
            {
                Debug.LogError(p.errorLog);
                p.Release(this);
                return;
            }

            //Release the previous path
            if (path != null) path.Release(this);

            //Replace the old path
            path = p;

            //Reset some variables
            currentWaypointIndex = 1;
            targetReached = false;
        }

        /** Rotates in the specified direction.
         * Rotates around the Y-axis.
         * \see turningSpeed
         */
        protected virtual void RotateTowards(Vector3 dir)
        {

            if (dir == Vector3.zero) return;

            Quaternion rot = tr.rotation;
            Quaternion toTarget = Quaternion.LookRotation(dir);

            rot = Quaternion.Slerp(rot, toTarget, turningSpeed * Time.deltaTime);
            Vector3 euler = rot.eulerAngles;
            euler.z = 0;
            euler.x = 0;
            rot = Quaternion.Euler(euler);

            tr.rotation = rot;
        }

        public virtual Vector3 GetFeetPosition()
        {
            return tr.position - Vector3.up * controller.height * 0.5F;
        }

        // Update is called once per frame
        void Update()
        {
            if (!canMove) { return; }

            if (path == null || path.vectorPath == null || path.vectorPath.Count == 0)
            {
                return;
            }
            List<Vector3> vPath = path.vectorPath;
            if(currentWaypointIndex < vPath.Count)
            {
                targetPoint = vPath[currentWaypointIndex];
            }
            targetDirection = targetPoint - tr.position;
            targetDirection.y = 0;

            RotateTowards(targetDirection);
            float relativeDistance = speed * Time.deltaTime;

            if (currentWaypointIndex == (vPath.Count -1))
            {
                float targetDistance = Vector3.Distance(tr.position, targetPoint);

                if (targetDistance > endReachedDistance)
                {
                    if(relativeDistance > (targetDistance - endReachedDistance))
                    {
                        relativeDistance = targetDistance - endReachedDistance;
                    }
                }
                else
                {
                    relativeDistance = 0;
                }
            }
            if(relativeDistance == 0)
            {
                targetReached = true;
            }
            Vector3 Movement = targetDirection.normalized * relativeDistance;

            Movement.y -= gravity * Time.deltaTime;
            controller.Move(Movement);

            if (Creature != null)
            {
                Creature.setTargetPosition(targetPoint);
                Creature.setPosition(tr.position);
            }
            if (currentWaypointIndex < (vPath.Count -1) && Vector3.Distance(tr.position, targetPoint) < pickNextWaypointDist)
            {
                currentWaypointIndex++;
                return;
            }
        }

        void OnDrawGizmos()
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawSphere(targetPoint, 1f);
        }
    }
}
