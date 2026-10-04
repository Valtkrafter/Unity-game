"""Small quaternion helpers, (x, y, z, w) order, Unity conventions (left-handed, same Hamilton product)."""
import numpy as np

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return np.array([aw*bx + ax*bw + ay*bz - az*by,
                     aw*by - ax*bz + ay*bw + az*bx,
                     aw*bz + ax*by - ay*bx + az*bw,
                     aw*bw - ax*bx - ay*by - az*bz])

def qinv(q):
    return np.array([-q[0], -q[1], -q[2], q[3]]) / np.dot(q, q)

def qrot(q, v):
    qv = np.array([v[0], v[1], v[2], 0.0])
    return qmul(qmul(q, qv), qinv(q))[:3]

def qnorm(q):
    return q / np.linalg.norm(q)

def qaxis(axis, ang):
    axis = np.asarray(axis, float); axis = axis / np.linalg.norm(axis)
    s = np.sin(ang / 2)
    return np.array([axis[0]*s, axis[1]*s, axis[2]*s, np.cos(ang / 2)])

def qfromto(a, b):
    a = np.asarray(a, float) / np.linalg.norm(a); b = np.asarray(b, float) / np.linalg.norm(b)
    d = np.dot(a, b)
    if d < -0.999999:
        perp = np.cross([1, 0, 0], a)
        if np.linalg.norm(perp) < 1e-6: perp = np.cross([0, 1, 0], a)
        return qaxis(perp, np.pi)
    c = np.cross(a, b)
    return qnorm(np.array([c[0], c[1], c[2], 1 + d]))

def qslerp(a, b, t):
    d = np.dot(a, b)
    if d < 0: b = -b; d = -d
    if d > 0.9995: return qnorm(a + t * (b - a))
    th = np.arccos(d)
    return (np.sin((1 - t) * th) * a + np.sin(t * th) * b) / np.sin(th)

def qangle(a, b):
    return 2 * np.degrees(np.arccos(min(1.0, abs(np.dot(qnorm(a), qnorm(b))))))

IDENT = np.array([0.0, 0.0, 0.0, 1.0])
