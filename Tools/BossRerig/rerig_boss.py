"""Re-skin + T-pose the Crimson Crowned King (Boss_Rigged.blend copy).
Run: blender -b Boss_Rigged.blend --python rerig_boss.py -- <out_dir>
Writes Boss_Rerigged.blend, renders check poses, exports Boss_Unity_Rerigged.fbx.
The source .blend is never saved over."""
import bpy, sys, math, os, statistics
from mathutils import Vector, Matrix, Quaternion

OUT = sys.argv[sys.argv.index("--") + 1]
os.makedirs(OUT, exist_ok=True)
arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
body = next(o for o in bpy.data.objects if o.type == 'MESH')
bones = arm.data.bones
H = lambda n: arm.matrix_world @ bones[n].head_local
T = lambda n: arm.matrix_world @ bones[n].tail_local
mw = body.matrix_world
verts = body.data.vertices
P = [mw @ v.co for v in verts]
groups = {g.name: g.index for g in body.vertex_groups}
gname = {g.index: g.name for g in body.vertex_groups}

def seg(p, a, b):
    ab = b - a; L2 = max(ab.length_squared, 1e-9)
    u = max(0.0, min(1.0, (p - a).dot(ab) / L2))
    return (p - (a + ab * u)).length, u

def dominant(v):
    gs = [g for g in v.groups if g.weight > 0.01 and gname.get(g.group) in bones]
    return gname[max(gs, key=lambda g: g.weight).group] if gs else None

orig_dom = [dominant(v) for v in verts]

# ---- limb chains (segment bones; the last one is extended to cover fingers/toes)
def chain_segments(names, extend):
    segs = []
    for i, n in enumerate(names):
        a, b = H(n), T(n)
        if i == len(names) - 1: b = b + (b - a).normalized() * extend
        segs.append((n, a, b))
    return segs
chains = {
    'LA': (chain_segments(['LeftUpperArm', 'LeftLowerArm', 'LeftHand'], 0.05), 'LeftShoulder'),
    'RA': (chain_segments(['RightUpperArm', 'RightLowerArm', 'RightHand'], 0.05), 'RightShoulder'),
    'LL': (chain_segments(['LeftUpperLeg', 'LeftLowerLeg', 'LeftFoot', 'LeftToes'], 0.03), 'Hips'),
    'RL': (chain_segments(['RightUpperLeg', 'RightLowerLeg', 'RightFoot', 'RightToes'], 0.03), 'Hips'),
}
# Limb radius per chain: robust spread of the vertices the original rig gave that limb.
radius = {}
for key, (segs, _) in chains.items():
    names = {s[0] for s in segs}
    ds = []
    for i, p in enumerate(P):
        if orig_dom[i] in names:
            ds.append(min(seg(p, a, b)[0] for _, a, b in segs))
    ds.sort()
    r = ds[int(len(ds) * 0.8)] if ds else 0.06
    radius[key] = max(0.035, min(0.11, r))
print("limb radii", {k: round(v, 3) for k, v in radius.items()})

hipsZ = H('Hips').z
kneeZ = (H('LeftLowerLeg').z + H('RightLowerLeg').z) * 0.5
hipLx, hipRx = H('LeftUpperLeg').x, H('RightUpperLeg').x
spine = [('Hips', H('Hips').z), ('Spine', H('Spine').z), ('Chest', H('Chest').z), ('Neck', H('Neck').z), ('Head', H('Head').z)]
cape_bones = {n for n in bones.keys() if n.startswith('Cape')}

def chain_weights(p, segs, parent):
    best = min(((seg(p, a, b), i) for i, (_, a, b) in enumerate(segs)), key=lambda x: x[0][0])
    (d, u), k = best
    w = {segs[k][0]: 1.0}
    blend = 0.18
    if u < blend:  # near the segment's root joint: share with the parent
        par = segs[k - 1][0] if k > 0 else parent
        s = 0.5 + 0.5 * (u / blend)
        w = {segs[k][0]: s, par: 1 - s}
    elif u > 1 - blend and k < len(segs) - 1:
        s = 0.5 + 0.5 * ((1 - u) / blend)
        w = {segs[k][0]: s, segs[k + 1][0]: 1 - s}
    return d, w

def torso_weights(z):
    # Linear blend between neighbouring spine bones by height.
    for i in range(len(spine) - 1):
        n0, z0 = spine[i]; n1, z1 = spine[i + 1]
        if z < z1:
            if i == 0 and z < z0: return {n0: 1.0}
            t = max(0.0, min(1.0, (z - z0) / max(z1 - z0, 1e-6)))
            # hold the lower bone for the first 60% of its span, then blend
            t = max(0.0, (t - 0.6) / 0.4)
            return {n0: 1 - t, n1: t} if t > 0 else {n0: 1.0}
    return {'Head': 1.0}

new_w = []
stats = {'arm': 0, 'leg': 0, 'skirt': 0, 'cape': 0, 'torso': 0}
for i, p in enumerate(P):
    w = None
    # arms first (pauldron spikes near the shoulder follow the arm — rigid plates)
    for key in ('LA', 'RA'):
        segs, parent = chains[key]
        side = 'Left' if key == 'LA' else 'Right'
        # Gate by the original rig: in the old arms-down pose the hands hung beside
        # the skirt's side panels — geometry only joins an arm if the old rig gave it
        # to that arm, or it is chest/shoulder mass above chest height.
        armish = orig_dom[i] in {s[0] for s in segs} or (orig_dom[i] in ('Chest', side + 'Shoulder') and p.z > H('Chest').z - 0.02)
        if not armish: continue
        d, cw = chain_weights(p, segs, parent)
        if d <= radius[key] * 1.25:
            w = cw; stats['arm'] += 1; break
    if w is None and p.z < hipsZ + 0.03:
        for key in ('LL', 'RL'):
            segs, parent = chains[key]
            d, cw = chain_weights(p, segs, parent)
            if d <= radius[key]:
                w = cw; stats['leg'] += 1; break
    if w is None and orig_dom[i] in cape_bones:
        w = {g: gw for g, gw in ((gname[g.group], g.weight) for g in verts[i].groups) if g in cape_bones or g == 'Chest'}
        if not w: w = {orig_dom[i]: 1.0}
        stats['cape'] += 1
    if w is None and p.z < hipsZ:
        # Skirt: pelvis-led, thighs take a share that grows toward the hem.
        t = max(0.0, min(1.0, (hipsZ - p.z) / max(hipsZ - kneeZ, 1e-6)))
        thigh = 0.6 * t
        sL = max(0.0, min(1.0, (p.x - hipRx) / max(hipLx - hipRx, 1e-6)))
        w = {'Hips': 1 - thigh, 'LeftUpperLeg': thigh * sL, 'RightUpperLeg': thigh * (1 - sL)}
        stats['skirt'] += 1
    if w is None:
        w = torso_weights(p.z); stats['torso'] += 1
    tot = sum(w.values())
    new_w.append({k: v / tot for k, v in w.items() if v > 1e-4})
print("assignment", stats)

# Stray forearm/hand armour chips the old rig gave to the body: small islands of
# non-arm vertices (connectivity ignoring arm vertices) whose centre sat inside an
# arm capsule in the original pose → that arm. Big skirt panels fail the size test.
arm_names = {s[0] for k in ('LA', 'RA') for s in chains[k][0]}
is_arm = [max(w, key=w.get) in arm_names for w in new_w]
adj = [[] for _ in verts]
for e in body.data.edges:
    a, b = e.vertices
    if is_arm[a] == is_arm[b]: adj[a].append(b); adj[b].append(a)
seen = [False] * len(verts); moved = 0
for s0 in range(len(verts)):
    if seen[s0] or is_arm[s0]: continue
    comp = []; stack = [s0]; seen[s0] = True
    while stack:
        x = stack.pop(); comp.append(x)
        for y in adj[x]:
            if not seen[y]: seen[y] = True; stack.append(y)
    if len(comp) > 120: continue
    c = sum((P[j] for j in comp), Vector()) / len(comp)
    for key in ('LA', 'RA'):
        segs, parent = chains[key]
        d, cw = chain_weights(c, segs, parent)
        if d <= radius[key] * 2.2:
            for j in comp:
                _, wj = chain_weights(P[j], segs, parent); tot = sum(wj.values())
                new_w[j] = {k: v / tot for k, v in wj.items()}
            moved += len(comp); break
print("stray arm chips reassigned:", moved)

for g in body.vertex_groups:
    if g.name in bones: g.remove(range(len(verts)))
for n in bones.keys():
    if n not in groups: body.vertex_groups.new(name=n)
vg = {g.name: g for g in body.vertex_groups}
for i, w in enumerate(new_w):
    for n, x in w.items(): vg[n].add([i], x, 'REPLACE')

# ---- T-pose: arms horizontal, elbows/wrists straight, legs straight down.
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='POSE')
for pb in arm.pose.bones: pb.rotation_mode = 'QUATERNION'; pb.rotation_quaternion = Quaternion(); pb.location = Vector()
bpy.context.view_layer.update()

def aim(name, target):
    pb = arm.pose.bones[name]
    head = pb.head.copy(); d = (pb.tail - pb.head).normalized()
    q = d.rotation_difference(target.normalized())
    M = Matrix.Translation(head) @ q.to_matrix().to_4x4() @ Matrix.Translation(-head)
    pb.matrix = M @ pb.matrix
    bpy.context.view_layer.update()
for side, sx in (('Left', 1), ('Right', -1)):
    aim(side + 'UpperArm', Vector((sx, 0, 0)))
    aim(side + 'LowerArm', Vector((sx, 0, 0)))
    aim(side + 'Hand', Vector((sx, 0, 0)))
    aim(side + 'UpperLeg', Vector((0, 0, -1)))
    aim(side + 'LowerLeg', Vector((0, 0, -1)))

# Apply: bake the deformed mesh, make the pose the new rest, re-bind.
bpy.ops.object.mode_set(mode='OBJECT')
bpy.context.view_layer.objects.active = body
mod = next(m for m in body.modifiers if m.type == 'ARMATURE')
bpy.ops.object.modifier_apply(modifier=mod.name)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode='POSE')
bpy.ops.pose.armature_apply(selected=False)
bpy.ops.object.mode_set(mode='OBJECT')
m = body.modifiers.new("Armature", 'ARMATURE'); m.object = arm

# Cut the welds: in the old arms-down sculpt the hands/forearms were fused to the
# hip/skirt and armpits. After the T-pose those bridging faces are long slivers that
# span an arm-owned and a body-owned vertex — delete them (glue, not geometry).
import bmesh
arm_bones = {s[0] for k in ('LA', 'RA') for s in chains[k][0]}
dom_new = [max(w, key=w.get) for w in new_w]
bm = bmesh.new(); bm.from_mesh(body.data); bm.verts.ensure_lookup_table(); bm.faces.ensure_lookup_table()
sc = body.matrix_world.to_scale().x
kill = []
for f in bm.faces:
    idx = [v.index for v in f.verts]
    armv = [dom_new[j] in arm_bones for j in idx]
    if all(armv) or not any(armv): continue
    longest = max((e.verts[0].co - e.verts[1].co).length for e in f.edges) * sc
    if longest > 0.06: kill.append(f)
print("weld faces removed:", len(kill))
bmesh.ops.delete(bm, geom=kill, context='FACES')
loose = [v for v in bm.verts if not v.link_faces]
bmesh.ops.delete(bm, geom=loose, context='VERTS')
bm.to_mesh(body.data); bm.free(); body.data.update()
for side in ('Left', 'Right'):
    d = (bones[side + 'UpperArm'].tail_local - bones[side + 'UpperArm'].head_local).normalized()
    print(side, "upper arm below horizontal after T-pose:", round(math.degrees(math.asin(-d.z)), 1))
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, "Boss_Rerigged.blend"), copy=True)

# ---- check renders: rest T-pose + stress poses (front / side)
scene = bpy.context.scene
scene.render.engine = 'BLENDER_WORKBENCH'
scene.display.shading.light = 'STUDIO'; scene.display.shading.color_type = 'TEXTURE'
scene.render.resolution_x, scene.render.resolution_y = 720, 900
scene.render.film_transparent = False
cam_data = bpy.data.cameras.new("chk"); cam_data.type = 'ORTHO'; cam_data.ortho_scale = 1.25
cam = bpy.data.objects.new("chk", cam_data); scene.collection.objects.link(cam); scene.camera = cam
def shoot(tag, view):
    if view == 'front': cam.location = (0, -3, 0.5); cam.rotation_euler = (math.radians(90), 0, 0)
    else: cam.location = (3, 0, 0.5); cam.rotation_euler = (math.radians(90), 0, math.radians(90))
    scene.render.filepath = os.path.join(OUT, f"Rerig_{tag}_{view}.png")
    bpy.ops.render.render(write_still=True)
def pose(rots):
    bpy.context.view_layer.objects.active = arm
    bpy.ops.object.mode_set(mode='POSE')
    for pb in arm.pose.bones: pb.rotation_mode = 'XYZ'; pb.rotation_euler = (0, 0, 0)
    for n, e in rots.items(): arm.pose.bones[n].rotation_euler = tuple(math.radians(x) for x in e)
    bpy.ops.object.mode_set(mode='OBJECT'); bpy.context.view_layer.update()
pose({}); shoot("rest", 'front'); shoot("rest", 'side')
stride = {'LeftUpperLeg': (-45, 0, 0), 'LeftLowerLeg': (50, 0, 0), 'RightUpperLeg': (30, 0, 0), 'RightLowerLeg': (20, 0, 0),
          'LeftUpperArm': (0, 0, 70), 'RightUpperArm': (0, 0, -70), 'RightLowerArm': (0, -60, 0), 'Spine': (10, 0, 0)}
pose(stride); shoot("stride", 'front'); shoot("stride", 'side')
raise_ = {'LeftUpperArm': (0, 0, -40), 'RightUpperArm': (0, 0, 40), 'Chest': (0, 25, 0), 'LeftUpperLeg': (25, 0, 0)}
pose(raise_); shoot("raise", 'front'); shoot("raise", 'side')
pose({})

# ---- export for Unity (same object set; UVs untouched)
bpy.ops.object.select_all(action='DESELECT')
# The source file keeps the armature hidden — hidden objects can't be selected,
# so a selection export silently dropped the skeleton.
for o in (arm, body): o.hide_set(False); o.hide_viewport = False; o.hide_select = False
arm.select_set(True); body.select_set(True); bpy.context.view_layer.objects.active = arm
assert arm.select_get() and body.select_get(), "export selection failed"
bpy.ops.export_scene.fbx(filepath=os.path.join(OUT, "Boss_Unity_Rerigged.fbx"), use_selection=True,
                         object_types={'ARMATURE', 'MESH'}, add_leaf_bones=False, bake_anim=False,
                         primary_bone_axis='Y', secondary_bone_axis='X', armature_nodetype='NULL',
                         axis_forward='-Z', axis_up='Y', apply_unit_scale=True, use_armature_deform_only=False,
                         mesh_smooth_type='FACE')
print("RERIG DONE")
