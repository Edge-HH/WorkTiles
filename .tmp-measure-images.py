from PIL import Image
im = Image.open(r"D:\Files\Codes\Projects\WorkTiles\WorkTiles\.tmp-bug-images\image10.png").convert("RGB")
w, h = im.size
px = im.load()

def is_border(r, g, b):
    return 80 < b < 220 and b > r + 20 and abs(r - g) < 40 and r < 140

# sample a few pixels
print("corners", px[0, 0], px[w//2, h//2], px[10, 10])
ys = []
for y in range(h):
    xs = [x for x in range(w) if is_border(*px[x, y])]
    if len(xs) > 8:
        ys.append((y, min(xs), max(xs), len(xs)))
print("border rows", len(ys))
if ys:
    print("first", ys[0], "mid", ys[len(ys)//2], "last", ys[-1])
    left = min(t[1] for t in ys)
    right = max(t[2] for t in ys)
    top = ys[0][0]
    bot = ys[-1][0]
    print("box", left, top, right, bot, "mid", (left+right)/2, (top+bot)/2)

# dark pill pixels inside the box
if ys:
    left = min(t[1] for t in ys)
    right = max(t[2] for t in ys)
    top = ys[0][0]
    bot = ys[-1][0]
    ink = []
    for y in range(top, bot):
        for x in range(left, right):
            r, g, b = px[x, y]
            if r < 50 and g < 50 and b < 55 and not is_border(r, g, b):
                ink.append((x, y))
    if ink:
        xs = [p[0] for p in ink]
        yy = [p[1] for p in ink]
        print("dark", min(xs), min(yy), max(xs), max(yy), "mid", (min(xs)+max(xs))/2, (min(yy)+max(yy))/2)
        print("gaps L", min(xs)-left, "R", right-max(xs), "T", min(yy)-top, "B", bot-max(yy))
