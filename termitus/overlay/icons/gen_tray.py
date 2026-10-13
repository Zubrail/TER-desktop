import sys, math
from PIL import Image, ImageDraw
from gen_icon import cubic, arc_top

def tray(size, bg, dot=(255,255,255,255)):
    S = size/512.0
    def T(x,y): return ((56+0.78*x)*S,(64+0.78*y)*S)
    img = Image.new("RGBA",(size,size),(0,0,0,0))
    m = Image.new("L",(size,size),0)
    ImageDraw.Draw(m).rounded_rectangle([0,0,size-1,size-1],radius=114*S,fill=255)
    fill = Image.new("RGBA",(size,size),bg)
    img.paste(fill,(0,0),m)
    d=ImageDraw.Draw(img)
    dome=cubic((64,400),(64,250),(150,116),(256,112))+cubic((256,112),(362,116),(448,250),(448,400))
    dome=[T(x,y) for x,y in dome]; dome.append(T(64,400))
    d.polygon(dome,fill=(255,255,255,255))
    arch=[T(212,400),T(212,330)]+[T(x,y) for x,y in arc_top(256,330,44)]+[T(300,400)]
    d.polygon(arch,fill=bg)
    icx,icy=T(256,356); ir=0.78*16*S
    d.ellipse([icx-ir,icy-ir,icx+ir,icy+ir],fill=dot)
    return img

# NotifyIcon1 = off (grey), NotifyIcon2 = connected (mint)
sizes=[16,20,24,32,48,64,128,256]
for name,bg in [("NotifyIcon1",(90,92,100,255)),("NotifyIcon2",(16,185,129,255)),
                ("NotifyIcon3",(90,92,100,255)),("NotifyIcon4",(16,185,129,255))]:
    imgs=[tray(s,bg) for s in sizes]
    imgs[-1].save(f"/tmp/{name}.ico", sizes=[(s,s) for s in sizes], append_images=imgs[:-1])
    print("wrote", name)
