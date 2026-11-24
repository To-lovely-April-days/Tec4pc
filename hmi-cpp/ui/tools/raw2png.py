import zlib,struct,sys
for f in sys.argv[1:]:
    d=open(f,'rb').read()
    if d[:4]==b'\x89PNG': continue
    w,h=2560,1440; rows=[]
    for y in range(h):
        r=bytearray(d[y*w*4:(y+1)*w*4]); r[0::4],r[2::4]=r[2::4],r[0::4]; r[3::4]=b'\xff'*w
        rows.append(b'\0'+bytes(r))
    ch=lambda t,b: struct.pack('>I',len(b))+t+b+struct.pack('>I',zlib.crc32(t+b))
    open(f,'wb').write(b'\x89PNG\r\n\x1a\n'+ch(b'IHDR',struct.pack('>IIBBBBB',w,h,8,6,0,0,0))+ch(b'IDAT',zlib.compress(b''.join(rows),6))+ch(b'IEND',b''))
