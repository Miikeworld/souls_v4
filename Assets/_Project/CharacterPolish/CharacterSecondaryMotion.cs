using System;
using UnityEngine;

[DefaultExecutionOrder(150)]
public sealed class CharacterSecondaryMotion : MonoBehaviour
{
    [Serializable] public sealed class Joint
    {
        public Transform bone;
        public float length = .2f;
        [Range(1,30)] public float angleLimit = 12;
        [Range(.1f,2)] public float stiffnessScale = 1;
        [NonSerialized] public Quaternion rest;
        [NonSerialized] public Vector3 offset, velocity;
    }
    public Joint[] joints = Array.Empty<Joint>();
    public Transform movementRoot;
    public Animator humanoid;
    [Min(5)] public float stiffness = 65;
    [Min(1)] public float damping = 15;
    [Range(0,1)] public float gravity = .12f;
    [Range(0,2)] public float inertia = .65f;
    [Min(0)] public float collisionRadius = .025f;
    [Range(0,2)] public float idleSway = .35f;
    public Material stylizedMaterial;
    public SkinnedMeshRenderer outline;
    public float MaximumAngle { get; private set; }
    public bool IsStable { get; private set; } = true;
    Vector3 previousPosition,previousVelocity;
    Quaternion previousRotation;
    MaterialPropertyBlock outlineProperties;
    bool initialized;
    void OnEnable()
    {
        if (!Application.isPlaying) return;
        if (!movementRoot) movementRoot=transform.root;
        foreach(var j in joints) if(j.bone){j.rest=j.bone.localRotation;j.offset=j.velocity=Vector3.zero;}
        previousPosition=movementRoot.position;previousRotation=movementRoot.rotation;previousVelocity=Vector3.zero;
        initialized=true;
    }
    void Update(){if(initialized)foreach(var j in joints)if(j.bone)j.bone.localRotation=j.rest;}
    void OnDisable(){if(initialized)foreach(var j in joints)if(j.bone)j.bone.localRotation=j.rest;initialized=false;}
    void LateUpdate()
    {
        if(outline && stylizedMaterial)
        {
            outlineProperties??=new MaterialPropertyBlock();
            outlineProperties.SetFloat("_OutlineThickness",stylizedMaterial.GetFloat("_OutlineThickness"));
            outlineProperties.SetColor("_OutlineColor",stylizedMaterial.GetColor("_OutlineColor"));
            outline.SetPropertyBlock(outlineProperties);
        }
        if(!initialized || Time.deltaTime<=0)return;
        float dt=Time.deltaTime;
        Vector3 delta=movementRoot.position-previousPosition;
        bool reset=dt>.15f || delta.magnitude>1.0f;
        Vector3 velocity=reset?Vector3.zero:delta/dt;
        Vector3 acceleration=Vector3.ClampMagnitude((velocity-previousVelocity)/dt,35);
        float turn=reset?0:Mathf.DeltaAngle(previousRotation.eulerAngles.y,movementRoot.eulerAngles.y)/dt;
        previousPosition=movementRoot.position;previousVelocity=velocity;previousRotation=movementRoot.rotation;
        MaximumAngle=0;
        for(int index=0;index<joints.Length;index++)
        {
            var j=joints[index];if(!j.bone)continue;
            Quaternion baseWorld=j.bone.parent.rotation*j.rest;
            Vector3 localForce=Quaternion.Inverse(baseWorld)*(-acceleration*inertia*.007f+Vector3.down*gravity);
            Vector3 target=new Vector3(localForce.z*20+Mathf.Sin(Time.time*1.6f+index*.7f)*idleSway,0,-localForce.x*20-turn*.009f*inertia);
            target=Vector3.ClampMagnitude(target,j.angleLimit);
            if(reset){j.offset=j.velocity=Vector3.zero;}
            int steps=Mathf.Clamp(Mathf.CeilToInt(dt*120),1,18);float h=dt/steps;
            for(int s=0;s<steps;s++)
            {
                j.velocity+=((target-j.offset)*stiffness*j.stiffnessScale-j.velocity*damping)*h;
                j.offset+=j.velocity*h;
                if(j.offset.magnitude>j.angleLimit){j.offset=Vector3.ClampMagnitude(j.offset,j.angleLimit);j.velocity*=.5f;}
            }
            Quaternion proposed=baseWorld*Quaternion.Euler(j.offset);
            Vector3 direction=proposed*Vector3.up;
            Vector3 tip=j.bone.position+direction*j.length*j.bone.lossyScale.y;
            if(humanoid && humanoid.isHuman)
            {
                tip=Collide(tip,HumanBodyBones.Hips,HumanBodyBones.Chest,.065f);
                tip=Collide(tip,HumanBodyBones.LeftUpperLeg,HumanBodyBones.LeftLowerLeg,.045f);
                tip=Collide(tip,HumanBodyBones.RightUpperLeg,HumanBodyBones.RightLowerLeg,.045f);
                Quaternion correction=Quaternion.FromToRotation(direction,(tip-j.bone.position).normalized);
                proposed=Quaternion.RotateTowards(baseWorld,correction*proposed,j.angleLimit);
            }
            if(float.IsNaN(proposed.x)||float.IsInfinity(proposed.x)){IsStable=false;j.offset=j.velocity=Vector3.zero;proposed=baseWorld;}
            j.bone.rotation=proposed;
            MaximumAngle=Mathf.Max(MaximumAngle,Quaternion.Angle(baseWorld,proposed));
        }
    }
    Vector3 Collide(Vector3 point,HumanBodyBones a,HumanBodyBones b,float radius)
    {
        Transform ta=humanoid.GetBoneTransform(a),tb=humanoid.GetBoneTransform(b);if(!ta||!tb)return point;
        Vector3 d=tb.position-ta.position;
        Vector3 closest=ta.position+d*Mathf.Clamp01(Vector3.Dot(point-ta.position,d)/Mathf.Max(.00001f,d.sqrMagnitude));
        Vector3 separation=point-closest;float r=radius*transform.lossyScale.y+collisionRadius;
        if(separation.sqrMagnitude<r*r)return closest+(separation.sqrMagnitude>.000001f?separation.normalized:-movementRoot.forward)*r;
        return point;
    }
}
