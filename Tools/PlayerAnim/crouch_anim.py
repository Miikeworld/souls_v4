"""Author crouch animations on the protagonist's own skeleton (Blender 4.5).

blender -b --python Tools/PlayerAnim/crouch_anim.py

Imports Assets/Character_Unity.fbx, poses a crouch with leg IK (feet stay on
their rest spots, knees driven forward by pole targets), bakes three actions
and exports each (armature only, one take per file) to
Assets/_Project/Animations/Crouch/Player_Crouch_<Down|Idle|Up>.fbx:
  Crouch_Down  (10 f)  standing-ish -> crouch with a small settle overshoot
  Crouch_Idle  (61 f)  breathing hold, loops (frame 61 == frame 1)
  Crouch_Up    ( 9 f)  crouch -> half rise (the Animator transition finishes it)
Check renders (front/side, rest vs crouch) go to Tools/PlayerAnim/.
Unity side: Tools > Project Restart > Player Model > Install Crouch Animations.
"""
import bpy, math, os
from mathutils import Vector, Matrix

ROOT = r"D:\souls_v4"
SRC = os.path.join(ROOT, r"Assets\Character_Unity.fbx")
OUT_DIR = os.path.join(ROOT, r"Tools\PlayerAnim")
FBX_OUT = os.path.join(ROOT, r"Assets\_Project\Animations\Crouch\Player_Crouch.fbx")
os.makedirs(OUT_DIR, exist_ok=True)
os.makedirs(os.path.dirname(FBX_OUT), exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=SRC, automatic_bone_orientation=False)
arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
mesh = next(o for o in bpy.data.objects if o.type == 'MESH')
scene = bpy.context.scene
scene.render.fps = 30
W = arm.matrix_world.copy()
Winv = W.inverted()
B = arm.data.bones

# Character frame (Blender world): faces -Y, up +Z, left = +X. Forward pitch = +X rotation.
FWD = Vector((0, -1, 0))
PITCH = Vector((1, 0, 0))
FLOOR = min((mesh.matrix_world @ v.co).z for v in mesh.data.vertices)

bpy.context.view_layer.objects.active = arm
arm.select_set(True)
bpy.ops.object.mode_set(mode='POSE')
for pb in arm.pose.bones:
    pb.rotation_mode = 'QUATERNION'

def world_of(bone_name, attr='head_local'):
    return W @ getattr(B[bone_name], attr)

def empty(name, matrix):
    e = bpy.data.objects.new(name, None)
    scene.collection.objects.link(e)
    e.matrix_world = matrix
    return e

# Rest ankle spots the solve must hit, and rest foot orientation (kept in world).
REST_ANKLE = {side: world_of(side + "Foot") for side in ("Left", "Right")}

def reset_pose():
    for pb in arm.pose.bones:
        pb.location = (0, 0, 0); pb.rotation_quaternion = (1, 0, 0, 0); pb.scale = (1, 1, 1)
    bpy.context.view_layer.update()

def rot_world(name, axis, deg):
    """Rotate a pose bone about its head by a WORLD-space axis."""
    bpy.context.view_layer.update()
    pb = arm.pose.bones[name]
    ax = (Winv.to_3x3() @ Vector(axis)).normalized()
    M = pb.matrix.copy()
    h = M.translation.copy()
    pb.matrix = Matrix.Translation(h) @ Matrix.Rotation(math.radians(deg), 4, ax) @ Matrix.Translation(-h) @ M
    bpy.context.view_layer.update()

def move_world(name, delta):
    bpy.context.view_layer.update()
    pb = arm.pose.bones[name]
    M = pb.matrix.copy()
    M.translation = M.translation + Winv.to_3x3() @ Vector(delta)
    pb.matrix = M
    bpy.context.view_layer.update()

# Full-crouch targets (k = 1); everything scales with k.
HIP_DROP, HIP_BACK, PELVIS = 0.205, 0.05, 14.0
SPINE, CHEST, NECK, HEAD = 9.0, 9.0, -13.0, -12.0
# Arms point DOWN, so +PITCH swings them backward: negative = forward. The torso
# lean (~32°) already carries them back, so they swing forward past vertical.
UPPER_ARM_PITCH, UPPER_ARM_IN, ELBOW = -46.0, 4.0, -30.0

def pose(k, breath=0.0):
    reset_pose()
    move_world("Hips", (0, HIP_BACK * k, -(HIP_DROP * k + 0.006 * breath)))
    rot_world("Hips", PITCH, PELVIS * k)
    rot_world("Spine", PITCH, SPINE * k)
    rot_world("Chest", PITCH, CHEST * k + 1.2 * breath)
    rot_world("Neck", PITCH, NECK * k)
    rot_world("Head", PITCH, HEAD * k - 0.8 * breath)
    for side, sgn in (("Left", 1), ("Right", -1)):
        rot_world(side + "UpperArm", PITCH, UPPER_ARM_PITCH * k)
        rot_world(side + "UpperArm", FWD, -sgn * UPPER_ARM_IN * k)  # tuck toward the body
        rot_world(side + "LowerArm", PITCH, ELBOW * k)
        solve_leg(side, PELVIS * k)

def head_of(name):
    return W @ arm.pose.bones[name].head

def yz_angle(u, v):
    """Signed angle (deg) rotating u onto v about +X, in the YZ plane."""
    return math.degrees(math.atan2(u.y * v.z - u.z * v.y, u.y * v.y + u.z * v.z))

def solve_leg(side, pelvis_deg):
    """Twist-free 2-bone solve: thigh and shin rotate ONLY about the lateral
    axis (+X), so Unity's humanoid reads pure Front-Back/Stretch muscles. The
    IK-constraint version twisted the thighs (pole angles 135/45 deg); Unity
    clamps leg twist, so the bend came out as splayed frog legs sunk in the floor.
    The ankle lands exactly on its rest spot; the foot keeps its world rotation."""
    bpy.context.view_layer.update()
    h, k0, a0 = head_of(side + "UpperLeg"), head_of(side + "LowerLeg"), head_of(side + "Foot")
    A = REST_ANKLE[side]
    l1 = math.hypot(k0.y - h.y, k0.z - h.z)
    l2 = math.hypot(a0.y - k0.y, a0.z - k0.z)
    dy, dz = A.y - h.y, A.z - h.z
    d = min(math.hypot(dy, dz), l1 + l2 - 1e-4)
    # knee = circle intersection; pick the FORWARD one (character faces -Y)
    along = (l1 * l1 - l2 * l2 + d * d) / (2 * d)
    off = math.sqrt(max(0.0, l1 * l1 - along * along))
    uy, uz = dy / d, dz / d
    cands = [(h.y + uy * along + s * -uz * off, h.z + uz * along + s * uy * off) for s in (1, -1)]
    ky, kz = min(cands, key=lambda c: c[0])
    alpha = yz_angle(k0 - h, Vector((0, ky - h.y, kz - h.z)))
    rot_world(side + "UpperLeg", PITCH, alpha)
    k1, a1 = head_of(side + "LowerLeg"), head_of(side + "Foot")
    beta = yz_angle(a1 - k1, A - k1)
    rot_world(side + "LowerLeg", PITCH, beta)
    rot_world(side + "Foot", PITCH, -(pelvis_deg + alpha + beta))

CONTROL = ["Hips", "Spine", "Chest", "Neck", "Head", "LeftUpperArm", "RightUpperArm", "LeftLowerArm", "RightLowerArm"]

def bake(name, keys):
    """keys: list of (k, breath) per frame starting at 1."""
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    arm.animation_data_create(); arm.animation_data.action = act
    for i, (k, br) in enumerate(keys):
        f = i + 1
        pose(k, br)
        for pb in arm.pose.bones:  # plain FK keys on every bone
            pb.keyframe_insert("rotation_quaternion", frame=f)
            pb.keyframe_insert("location", frame=f)
            pb.keyframe_insert("scale", frame=f)
    return act

down_k = [0.35, 0.55, 0.72, 0.85, 0.95, 1.03, 1.05, 1.03, 1.01, 1.0]
idle = [(1.0, math.sin(2 * math.pi * i / 60.0)) for i in range(61)]
up_k = [1.0, 0.98, 0.9, 0.78, 0.62, 0.46, 0.32, 0.2, 0.12]
a_down = bake("Crouch_Down", [(k, 0.0) for k in down_k])
a_idle = bake("Crouch_Idle", idle)
a_up = bake("Crouch_Up", [(k, 0.0) for k in up_k])

# --- checks: feet planted, crouch depth -------------------------------------
def foot_report(action, frame):
    arm.animation_data.action = action
    scene.frame_set(frame)
    out = []
    for side in ("Left", "Right"):
        a = W @ arm.pose.bones[side + "Foot"].head
        r = world_of(side + "Foot")
        kx = (W @ arm.pose.bones[side + "LowerLeg"].head).x - world_of(side + "LowerLeg").x
        out.append(f"{side} ankle drift {(a - r).length * 1000:.1f}mm knee splay {kx * 1000:+.1f}mm")
    hips = W @ arm.pose.bones["Hips"].head
    head = W @ arm.pose.bones["Head"].tail
    return f"{action.name}@{frame}: " + ", ".join(out) + f", hips {hips.z - FLOOR:.3f} head {head.z - FLOOR:.3f} above floor"

# --- remove the rig, render checks --------------------------------------------
# Checks run AFTER the rig is gone, so they measure exactly what gets exported.
for side in ("Left", "Right"):
    for bn in (side + "LowerLeg", side + "Foot"):
        pb = arm.pose.bones[bn]
        for c in list(pb.constraints):
            pb.constraints.remove(c)
for o in [o for o in bpy.data.objects if o.type == 'EMPTY']:
    bpy.data.objects.remove(o)
print("CHECK", foot_report(a_down, 1)); print("CHECK", foot_report(a_idle, 1)); print("CHECK", foot_report(a_idle, 31))
print("CHECK", foot_report(a_up, 9))

bpy.ops.object.mode_set(mode='OBJECT')
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'STUDIO'
scene.display.shading.color_type = 'SINGLE'
scene.render.resolution_x, scene.render.resolution_y = 420, 560
bpy.ops.mesh.primitive_plane_add(size=1.6, location=(0, 0, FLOOR))
cam_data = bpy.data.cameras.new("cam"); cam_data.type = 'ORTHO'; cam_data.ortho_scale = 1.25
cam = bpy.data.objects.new("cam", cam_data); scene.collection.objects.link(cam); scene.camera = cam
mid = FLOOR + 0.5
def shoot(label, action, frame):
    if action is None:
        arm.animation_data.action = None
        for pb in arm.pose.bones:
            pb.location = (0, 0, 0); pb.rotation_quaternion = (1, 0, 0, 0)
    else:
        arm.animation_data.action = action
    scene.frame_set(frame)
    for view, loc, rot in (("front", (0, -4, mid), (math.radians(90), 0, 0)),
                           ("side", (-4, 0, mid), (math.radians(90), 0, math.radians(-90))),
                           ("three_quarter", (-2.83, -2.83, mid), (math.radians(90), 0, math.radians(-45)))):
        cam.location = loc; cam.rotation_euler = rot
        scene.render.filepath = os.path.join(OUT_DIR, f"Crouch_{label}_{view}.png")
        bpy.ops.render.render(write_still=True)
shoot("Rest", None, 1)
shoot("Hold", a_idle, 1)
shoot("DownStart", a_down, 1)

# --- export ---------------------------------------------------------------------
for o in bpy.data.objects:
    o.select_set(o == arm)
bpy.context.view_layer.objects.active = arm
# One FBX per take. The exporter's all-actions path re-assigns actions itself
# and, with Blender 4.4+ action slots, only the first take evaluated — the
# others came out frozen on one pose. Here each action (and its slot) is
# assigned explicitly and the scene animation is baked over frames 1..N.
# A rest key at frame 0 + exporting from frame 0's state keeps the file's
# default skeleton = the real rest pose (with no skin, bone defaults are
# written from the current pose; a posed default skews Unity's avatar copy).
REST = {"location": (0.0, 0.0, 0.0), "rotation_quaternion": (1.0, 0.0, 0.0, 0.0), "scale": (1.0, 1.0, 1.0)}
def add_rest_key(action):
    for fc in action.fcurves:
        prop = fc.data_path.rsplit(".", 1)[-1]
        if prop in REST:
            fc.keyframe_points.insert(0.0, REST[prop][fc.array_index], options={'FAST'})
            fc.update()
exported = []
for action in (a_down, a_idle, a_up):
    n = int(round(action.frame_range[1]))
    add_rest_key(action)
    arm.animation_data.action = action
    if hasattr(arm.animation_data, "action_slot") and arm.animation_data.action_slot is None and len(action.slots):
        arm.animation_data.action_slot = action.slots[0]
    scene.frame_start, scene.frame_end = 1, n
    scene.frame_set(0)
    path = FBX_OUT.replace(".fbx", "_" + action.name.split("_", 1)[1] + ".fbx")
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={'ARMATURE'},
                             add_leaf_bones=False, bake_anim=True, bake_anim_use_all_actions=False,
                             bake_anim_use_nla_strips=False, bake_anim_force_startend_keying=True,
                             bake_anim_simplify_factor=0.0, apply_unit_scale=True, global_scale=1.0,
                             # Match Character_Unity.fbx: UnitScaleFactor 100, unscaled nodes. The default
                             # (unit 1 + a x100 armature node) made Unity's humanoid read the hips at the
                             # wrong scale - the crouch flew the body away.
                             apply_scale_options='FBX_SCALE_UNITS')
    exported.append(path)
print("EXPORTED", exported)
