// Pure managed C# NaCl box (Curve25519 + XSalsa20-Poly1305).
// Compatible with libsodium / TweetNaCl. No external dependencies.
using System;
using System.Security.Cryptography;

namespace ART.ADK
{
    internal static class Salsa20
    {
        static readonly byte[] Sigma = { 101,120,112,97,110,100,32,51,50,45,98,121,116,101,32,107 };
        static uint RL(uint x,int c)=>(x<<c)|(x>>(32-c));
        static uint L32(byte[] b,int i)=>(uint)b[i]|((uint)b[i+1]<<8)|((uint)b[i+2]<<16)|((uint)b[i+3]<<24);
        static void S32(byte[] b,int i,uint v){b[i]=(byte)v;b[i+1]=(byte)(v>>8);b[i+2]=(byte)(v>>16);b[i+3]=(byte)(v>>24);}

        static void Core20(byte[] o, byte[] k, byte[] n)
        {
            uint x0=L32(Sigma,0),x1=L32(k,0),x2=L32(k,4),x3=L32(k,8),x4=L32(k,12),
                 x5=L32(Sigma,4),x6=L32(n,0),x7=L32(n,4),x8=L32(n,8),x9=L32(n,12),
                 x10=L32(Sigma,8),x11=L32(k,16),x12=L32(k,20),x13=L32(k,24),x14=L32(k,28),x15=L32(Sigma,12);
            uint j0=x0,j1=x1,j2=x2,j3=x3,j4=x4,j5=x5,j6=x6,j7=x7,
                 j8=x8,j9=x9,j10=x10,j11=x11,j12=x12,j13=x13,j14=x14,j15=x15;
            for(int i=20;i>0;i-=2){
                x4^=RL(x0+x12,7);x8^=RL(x4+x0,9);x12^=RL(x8+x4,13);x0^=RL(x12+x8,18);
                x9^=RL(x5+x1,7);x13^=RL(x9+x5,9);x1^=RL(x13+x9,13);x5^=RL(x1+x13,18);
                x14^=RL(x10+x6,7);x2^=RL(x14+x10,9);x6^=RL(x2+x14,13);x10^=RL(x6+x2,18);
                x3^=RL(x15+x11,7);x7^=RL(x3+x15,9);x11^=RL(x7+x3,13);x15^=RL(x11+x7,18);
                x1^=RL(x0+x3,7);x2^=RL(x1+x0,9);x3^=RL(x2+x1,13);x0^=RL(x3+x2,18);
                x6^=RL(x5+x4,7);x7^=RL(x6+x5,9);x4^=RL(x7+x6,13);x5^=RL(x4+x7,18);
                x11^=RL(x10+x9,7);x8^=RL(x11+x10,9);x9^=RL(x8+x11,13);x10^=RL(x9+x8,18);
                x12^=RL(x15+x14,7);x13^=RL(x12+x15,9);x14^=RL(x13+x12,13);x15^=RL(x14+x13,18);
            }
            S32(o,0,x0+j0);S32(o,4,x1+j1);S32(o,8,x2+j2);S32(o,12,x3+j3);
            S32(o,16,x4+j4);S32(o,20,x5+j5);S32(o,24,x6+j6);S32(o,28,x7+j7);
            S32(o,32,x8+j8);S32(o,36,x9+j9);S32(o,40,x10+j10);S32(o,44,x11+j11);
            S32(o,48,x12+j12);S32(o,52,x13+j13);S32(o,56,x14+j14);S32(o,60,x15+j15);
        }

        // HSalsa20: outputs x[0,5,10,15,6,7,8,9] without adding initial values
        public static void HSalsa20(byte[] @out, byte[] n16, byte[] k32)
        {
            uint x0=L32(Sigma,0),x1=L32(k32,0),x2=L32(k32,4),x3=L32(k32,8),x4=L32(k32,12),
                 x5=L32(Sigma,4),x6=L32(n16,0),x7=L32(n16,4),x8=L32(n16,8),x9=L32(n16,12),
                 x10=L32(Sigma,8),x11=L32(k32,16),x12=L32(k32,20),x13=L32(k32,24),x14=L32(k32,28),x15=L32(Sigma,12);
            for(int i=20;i>0;i-=2){
                x4^=RL(x0+x12,7);x8^=RL(x4+x0,9);x12^=RL(x8+x4,13);x0^=RL(x12+x8,18);
                x9^=RL(x5+x1,7);x13^=RL(x9+x5,9);x1^=RL(x13+x9,13);x5^=RL(x1+x13,18);
                x14^=RL(x10+x6,7);x2^=RL(x14+x10,9);x6^=RL(x2+x14,13);x10^=RL(x6+x2,18);
                x3^=RL(x15+x11,7);x7^=RL(x3+x15,9);x11^=RL(x7+x3,13);x15^=RL(x11+x7,18);
                x1^=RL(x0+x3,7);x2^=RL(x1+x0,9);x3^=RL(x2+x1,13);x0^=RL(x3+x2,18);
                x6^=RL(x5+x4,7);x7^=RL(x6+x5,9);x4^=RL(x7+x6,13);x5^=RL(x4+x7,18);
                x11^=RL(x10+x9,7);x8^=RL(x11+x10,9);x9^=RL(x8+x11,13);x10^=RL(x9+x8,18);
                x12^=RL(x15+x14,7);x13^=RL(x12+x15,9);x14^=RL(x13+x12,13);x15^=RL(x14+x13,18);
            }
            S32(@out,0,x0);S32(@out,4,x5);S32(@out,8,x10);S32(@out,12,x15);
            S32(@out,16,x6);S32(@out,20,x7);S32(@out,24,x8);S32(@out,28,x9);
        }

        public static void XSalsa20XOR(byte[] c, byte[] m, int mlen, byte[] n24, byte[] k32)
        {
            var sub=new byte[32]; var n16=new byte[16]; Array.Copy(n24,0,n16,0,16);
            HSalsa20(sub,n16,k32);
            var z=new byte[16]; Array.Copy(n24,16,z,0,8); // z[0..7]=n24[16..23], z[8..15]=counter=0
            var blk=new byte[64]; int pos=0;
            while(mlen>0){
                Core20(blk,sub,z);
                int t=Math.Min(mlen,64);
                for(int i=0;i<t;i++) c[pos+i]=(byte)(m[pos+i]^blk[i]);
                pos+=t; mlen-=t;
                int carry=1; for(int i=8;i<16&&carry>0;i++){int s=z[i]+carry;z[i]=(byte)s;carry=s>>8;}
            }
        }

        public static void XSalsa20Stream(byte[] c, int clen, byte[] n24, byte[] k32)
        {
            var m=new byte[clen]; XSalsa20XOR(c,m,clen,n24,k32);
        }
    }

    internal static class Poly1305
    {
        static uint R32(byte[] b,int i)=>(uint)b[i]|((uint)b[i+1]<<8)|((uint)b[i+2]<<16)|((uint)b[i+3]<<24);
        static void W32(byte[] b,int i,ulong v){b[i]=(byte)v;b[i+1]=(byte)(v>>8);b[i+2]=(byte)(v>>16);b[i+3]=(byte)(v>>24);}

        public static void Auth(byte[] tag, byte[] m, int moff, int mlen, byte[] key)
        {
            var r=new uint[5]; var h=new uint[5]; var s=new uint[4];
            r[0]= R32(key,0)      &0x3ffffffu;
            r[1]=(R32(key,3)>> 2)&0x3ffff03u;
            r[2]=(R32(key,6)>> 4)&0x3ffc0ffu;
            r[3]=(R32(key,9)>> 6)&0x3f03fffu;
            r[4]=(R32(key,12)>>8)&0x00fffffu;
            s[0]=R32(key,16);s[1]=R32(key,20);s[2]=R32(key,24);s[3]=R32(key,28);
            uint s1=r[1]*5,s2=r[2]*5,s3=r[3]*5,s4=r[4]*5;

            int pos=moff;
            while(mlen>0){
                int take=Math.Min(mlen,16);
                var blk=new byte[17]; Array.Copy(m,pos,blk,0,take); blk[take]=1;
                ulong t0=R32(blk,0),t1=R32(blk,4),t2=R32(blk,8),t3=R32(blk,12);
                h[0]+=(uint)(t0&0x3ffffffu);
                h[1]+=(uint)((t0>>26|t1<<6)&0x3ffffffu);
                h[2]+=(uint)((t1>>20|t2<<12)&0x3ffffffu);
                h[3]+=(uint)((t2>>14|t3<<18)&0x3ffffffu);
                h[4]+=(uint)(t3>>8)|(take==16?1u<<24:0);
                ulong d0=(ulong)h[0]*r[0]+(ulong)h[1]*s4+(ulong)h[2]*s3+(ulong)h[3]*s2+(ulong)h[4]*s1;
                ulong d1=(ulong)h[0]*r[1]+(ulong)h[1]*r[0]+(ulong)h[2]*s4+(ulong)h[3]*s3+(ulong)h[4]*s2;
                ulong d2=(ulong)h[0]*r[2]+(ulong)h[1]*r[1]+(ulong)h[2]*r[0]+(ulong)h[3]*s4+(ulong)h[4]*s3;
                ulong d3=(ulong)h[0]*r[3]+(ulong)h[1]*r[2]+(ulong)h[2]*r[1]+(ulong)h[3]*r[0]+(ulong)h[4]*s4;
                ulong d4=(ulong)h[0]*r[4]+(ulong)h[1]*r[3]+(ulong)h[2]*r[2]+(ulong)h[3]*r[1]+(ulong)h[4]*r[0];
                ulong c; c=d0>>26;h[0]=(uint)(d0&0x3ffffffu);d1+=c;
                c=d1>>26;h[1]=(uint)(d1&0x3ffffffu);d2+=c;
                c=d2>>26;h[2]=(uint)(d2&0x3ffffffu);d3+=c;
                c=d3>>26;h[3]=(uint)(d3&0x3ffffffu);d4+=c;
                c=d4>>26;h[4]=(uint)(d4&0x3ffffffu);h[0]+=(uint)(c*5);
                c=h[0]>>26;h[0]&=0x3ffffffu;h[1]+=(uint)c;
                pos+=take; mlen-=take;
            }
            uint c2; c2=h[1]>>26;h[1]&=0x3ffffffu;h[2]+=c2;
            c2=h[2]>>26;h[2]&=0x3ffffffu;h[3]+=c2;
            c2=h[3]>>26;h[3]&=0x3ffffffu;h[4]+=c2;
            c2=h[4]>>26;h[4]&=0x3ffffffu;h[0]+=c2*5;
            c2=h[0]>>26;h[0]&=0x3ffffffu;h[1]+=c2;
            uint g0=h[0]+5;c2=g0>>26;g0&=0x3ffffffu;
            uint g1=h[1]+c2;c2=g1>>26;g1&=0x3ffffffu;
            uint g2=h[2]+c2;c2=g2>>26;g2&=0x3ffffffu;
            uint g3=h[3]+c2;c2=g3>>26;g3&=0x3ffffffu;
            uint g4=h[4]+c2-(1u<<26);
            uint mask=~((uint)((int)g4>>31)); // 0xFFFFFFFF if h>=p else 0
            g0=(h[0]&~mask)|(g0&mask);g1=(h[1]&~mask)|(g1&mask);
            g2=(h[2]&~mask)|(g2&mask);g3=(h[3]&~mask)|(g3&mask);g4=(h[4]&~mask)|(g4&mask);
            ulong f0=((ulong)g0|(ulong)g1<<26)&0xffffffffu;
            ulong f1=((ulong)g1>>6|(ulong)g2<<20)&0xffffffffu;
            ulong f2=((ulong)g2>>12|(ulong)g3<<14)&0xffffffffu;
            ulong f3=((ulong)g3>>18|(ulong)g4<<8)&0xffffffffu;
            f0+=s[0];ulong cy=f0>>32;f0&=0xffffffffu;
            f1+=s[1]+cy;cy=f1>>32;f1&=0xffffffffu;
            f2+=s[2]+cy;cy=f2>>32;f2&=0xffffffffu;
            f3+=s[3]+cy;
            W32(tag,0,f0);W32(tag,4,f1);W32(tag,8,f2);W32(tag,12,f3);
        }

        public static bool Verify(byte[] tag, byte[] m, int moff, int mlen, byte[] key)
        {
            var computed=new byte[16]; Auth(computed,m,moff,mlen,key);
            int d=0; for(int i=0;i<16;i++) d|=computed[i]^tag[i]; return d==0;
        }
    }

    internal static class Curve25519
    {
        static long[] Gf()=>new long[16];
        static long[] Gf1(){var f=Gf();f[0]=1;return f;}

        static void Car(long[] o){
            for(int i=0;i<16;i++){
                o[i]+=(1L<<16); long c=o[i]>>16;
                if(i<15)o[i+1]+=c-1; else o[0]+=38*(c-1);
                o[i]-=c<<16;
            }
        }

        static void Sel(long[] p,long[] q,long b){
            long c=~(b-1);
            for(int i=0;i<16;i++){long t=c&(p[i]^q[i]);p[i]^=t;q[i]^=t;}
        }

        static void Pack(byte[] o,long[] n){
            var t=(long[])n.Clone();
            Car(t);Car(t);Car(t);
            for(int j=0;j<2;j++){
                var m=Gf(); m[0]=t[0]-0xffed;
                for(int i=1;i<15;i++){m[i]=t[i]-0xffff-((m[i-1]>>16)&1);m[i-1]&=0xffff;}
                m[15]=t[15]-0x7fff-((m[14]>>16)&1);
                long b=(m[15]>>16)&1; m[14]&=0xffff; Sel(t,m,1-b);
            }
            for(int i=0;i<16;i++){o[2*i]=(byte)t[i];o[2*i+1]=(byte)(t[i]>>8);}
        }

        static void Unpack(long[] o,byte[] n){
            for(int i=0;i<16;i++) o[i]=(long)n[2*i]|(((long)n[2*i+1])<<8);
            o[15]&=0x7fff;
        }

        static void A(long[] o,long[] a,long[] b){for(int i=0;i<16;i++)o[i]=a[i]+b[i];}
        static void Z(long[] o,long[] a,long[] b){for(int i=0;i<16;i++)o[i]=a[i]-b[i];}
        static void M(long[] o,long[] a,long[] b){
            var t=new long[31];
            for(int i=0;i<16;i++) for(int j=0;j<16;j++) t[i+j]+=a[i]*b[j];
            for(int i=0;i<15;i++) t[i]+=38*t[i+16];
            Array.Copy(t,o,16); Car(o);Car(o);
        }
        static void S(long[] o,long[] a)=>M(o,a,a);
        static void Inv(long[] o,long[] i){
            var c=(long[])i.Clone();
            for(int a=253;a>=0;a--){S(c,c);if(a!=2&&a!=4)M(c,c,i);}
            Array.Copy(c,o,16);
        }

        static long[] C121665(){var f=Gf();f[0]=0xDB41;f[1]=1;return f;}

        static void ScalarMult(byte[] q,byte[] n,byte[] p){
            var x=Gf(); Unpack(x,p);
            var a=Gf1();var b=Gf();var c=Gf();var d=Gf1();
            var e=Gf();var f=Gf();
            for(int i=0;i<16;i++) b[i]=x[i]; // b = input point
            for(int i=254;i>=0;i--){
                long bit=(n[i>>3]>>(i&7))&1L;
                Sel(a,b,bit);Sel(c,d,bit);
                A(e,a,c);Z(a,a,c);A(c,b,d);Z(b,b,d);
                S(d,e);S(f,a);M(a,c,a);M(c,b,e);
                A(e,a,c);Z(a,a,c);S(b,a);Z(c,d,f);
                M(a,c,C121665());A(a,a,d);M(c,c,a);
                M(a,d,f);M(d,b,x);S(b,e);
                Sel(a,b,bit);Sel(c,d,bit);
            }
            Inv(c,c);M(a,a,c);Pack(q,a);
        }

        public static void ScalarMultBase(byte[] q,byte[] sk){
            var bp=new byte[32];bp[0]=9;
            var n=new byte[32];Array.Copy(sk,n,32);
            n[0]&=248;n[31]&=127;n[31]|=64;
            ScalarMult(q,n,bp);
        }

        public static void DH(byte[] shared,byte[] sk,byte[] pk){
            var n=new byte[32];Array.Copy(sk,n,32);
            n[0]&=248;n[31]&=127;n[31]|=64;
            ScalarMult(shared,n,pk);
        }
    }

    internal static class NaclBox
    {
        public const int OverheadBytes=16;
        const int ZeroBytes=32;

        static byte[] BoxKey(byte[] pk,byte[] sk){
            var shared=new byte[32]; Curve25519.DH(shared,sk,pk);
            var k=new byte[32]; var z=new byte[16]; Salsa20.HSalsa20(k,z,shared); return k;
        }

        public static void KeyPair(out byte[] pk,out byte[] sk){
            sk=new byte[32]; using var rng=new RNGCryptoServiceProvider(); rng.GetBytes(sk);
            pk=new byte[32]; Curve25519.ScalarMultBase(pk,sk);
        }

        public static byte[] Box(byte[] m,byte[] nonce,byte[] pk,byte[] sk){
            var k=BoxKey(pk,sk);
            var mpad=new byte[m.Length+ZeroBytes]; Array.Copy(m,0,mpad,ZeroBytes,m.Length);
            var cpad=new byte[mpad.Length]; Salsa20.XSalsa20XOR(cpad,mpad,mpad.Length,nonce,k);
            // cpad[0..31]=keystream (poly1305 key), cpad[32..]=ciphertext
            var tag=new byte[16]; Poly1305.Auth(tag,cpad,ZeroBytes,m.Length,cpad);
            var result=new byte[OverheadBytes+m.Length];
            Array.Copy(tag,0,result,0,16);
            Array.Copy(cpad,ZeroBytes,result,OverheadBytes,m.Length);
            return result;
        }

        public static byte[] Open(byte[] box,byte[] nonce,byte[] pk,byte[] sk){
            if(box.Length<OverheadBytes) throw new EncryptionHelperException(EncryptionError.DataTooShort);
            var k=BoxKey(pk,sk);
            int mlen=box.Length-OverheadBytes;
            // Reconstruct cpad=[zeros32|ciphertext]
            var cpad=new byte[ZeroBytes+mlen]; Array.Copy(box,OverheadBytes,cpad,ZeroBytes,mlen);
            // Get poly1305 key from first keystream block
            var poly1305Key=new byte[32]; Salsa20.XSalsa20Stream(poly1305Key,32,nonce,k);
            var storedTag=new byte[16]; Array.Copy(box,0,storedTag,0,16);
            if(!Poly1305.Verify(storedTag,cpad,ZeroBytes,mlen,poly1305Key))
                throw new EncryptionHelperException(EncryptionError.AuthenticationFailed);
            var mpad=new byte[cpad.Length]; Salsa20.XSalsa20XOR(mpad,cpad,cpad.Length,nonce,k);
            var plain=new byte[mlen]; Array.Copy(mpad,ZeroBytes,plain,0,mlen); return plain;
        }
    }
}
