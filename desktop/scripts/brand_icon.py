"""Shared vector geometry for Xike's Windows and Android icons."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
BLUE = '#315BCE'
WHITE = '#FFF8EC'
GOLD = '#FFD38A'
MOON = 'M66,27 C43,20 24,40 28,61 C32,78 60,85 78,69 C58,74 42,60 45,44 C48,34 56,28 66,27 Z'
SEGMENTS = [((66,27),(43,20),(24,40),(28,61)),
            ((28,61),(32,78),(60,85),(78,69)),
            ((78,69),(58,74),(42,60),(45,44)),
            ((45,44),(48,34),(56,28),(66,27))]

def render(size):
    k = size * 8 / 108
    img = Image.new('RGBA', (size*8, size*8))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((0,0,size*8-1,size*8-1), radius=24*k, fill=BLUE)
    points = []
    for a,b,c,e in SEGMENTS:
        for i in range(65):
            t = i/64
            points.append(tuple(k*((1-t)**3*a[j]+3*(1-t)**2*t*b[j]+3*(1-t)*t*t*c[j]+t**3*e[j]) for j in (0,1)))
    d.polygon(points, fill=WHITE)
    d.line([(54*k,41*k),(54*k,54*k),(65*k,61*k)], fill=GOLD, width=round(5*k), joint='curve')
    for x,y in [(54,41),(54,54),(65,61)]:
        d.ellipse(((x-2.5)*k,(y-2.5)*k,(x+2.5)*k,(y+2.5)*k), fill=GOLD)
    return img.resize((size,size), Image.Resampling.LANCZOS)

def export():
    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 108 108">
<rect width="108" height="108" rx="24" fill="{BLUE}"/>
<path d="{MOON}" fill="{WHITE}"/>
<path d="M54 41V54L65 61" fill="none" stroke="{GOLD}" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>
</svg>'''
    (ROOT/'Assets/Xike.svg').write_text(svg,encoding='utf-8')
    render(512).save(ROOT/'Assets/Xike.png')
    android = ROOT.parent/'TimeGuardAndroid/app/src/main/res'
    vector = f'''<vector xmlns:android="http://schemas.android.com/apk/res/android"
    android:width="108dp" android:height="108dp" android:viewportWidth="108" android:viewportHeight="108">
    <path android:fillColor="{WHITE}" android:pathData="{MOON}"/>
    <path android:fillColor="#00000000" android:strokeColor="{GOLD}" android:strokeWidth="5"
        android:strokeLineCap="round" android:strokeLineJoin="round" android:pathData="M54,41 L54,54 L65,61"/>
</vector>
'''
    (android/'drawable/ic_launcher_foreground.xml').write_text(vector,encoding='utf-8')
    colors = android/'values/colors.xml'
    import re
    colors.write_text(re.sub(r'(<color name="ic_launcher_background">)[^<]+', rf'\g<1>{BLUE}', colors.read_text(encoding='utf-8')),encoding='utf-8')
    monochrome = vector.replace(WHITE,'#FFFFFF').replace(GOLD,'#FFFFFF')
    (android/'drawable/ic_launcher_monochrome.xml').write_text(monochrome,encoding='utf-8')
    (android/'drawable/ic_stat_timeguard.xml').write_text(monochrome,encoding='utf-8')
    (android/'drawable/ic_stat_xike.xml').write_text(monochrome,encoding='utf-8')
    background = f'''<path android:fillColor="{BLUE}" android:pathData="M24,0 H84 Q108,0 108,24 V84 Q108,108 84,108 H24 Q0,108 0,84 V24 Q0,0 24,0 Z"/>'''
    notification = vector.replace('    <path', '    ' + background + '\n    <path', 1)
    (android/'drawable/ic_notification_xike.xml').write_text(notification,encoding='utf-8')
    adaptive = android/'mipmap-anydpi-v26/ic_launcher.xml'
    adaptive.write_text('''<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@color/ic_launcher_background" />
    <foreground android:drawable="@drawable/ic_launcher_foreground" />
    <monochrome android:drawable="@drawable/ic_launcher_monochrome" />
</adaptive-icon>
''',encoding='utf-8')
    (android/'mipmap-anydpi-v26/ic_launcher_xike.xml').write_text(adaptive.read_text(encoding='utf-8'),encoding='utf-8')

if __name__ == '__main__':
    export()
