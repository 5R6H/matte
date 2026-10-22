// Native Direct3D 11 renderer for Matte's Spatial desktop layer (SpatialDesktop.cs, native path).
// Draws a .spatial file the way Spatial Photos' web viewer (viewer.js, three.js; MIT) does: same mesh, textures, blending,
// camera and contain fit, so a file looks the same in both. Unlike the web viewer it reads the cursor itself, draws only while the camera
// moves, and does its own cross-fades (slide to slide, and to/from a flat still of the photo for fade in/out).
// Built by build.ps1 into SpatialNative.dll next to the exe.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11_4.h>
#include <dxgi1_3.h>
#include <d3dcompiler.h>
#include <wincodec.h>
#include <dcomp.h>
#include <wrl/client.h>
#include <vector>
#include <string>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <atomic>
#include <memory>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <limits>
#include <new>
#include <stdexcept>
#pragma comment(lib,"d3d11.lib")
#pragma comment(lib,"dxgi.lib")
#pragma comment(lib,"d3dcompiler.lib")
#pragma comment(lib,"windowscodecs.lib")
#pragma comment(lib,"dcomp.lib")
#pragma comment(lib,"ole32.lib")
#pragma comment(lib,"user32.lib")
using Microsoft::WRL::ComPtr;

typedef void (__stdcall *SnCallback)(int kind,int sequence);
enum { EvFirstScene=1, EvTransitionDone=2, EvLoadError=3, EvFadeOutDone=4, EvLoadFailed=5 };
// Public reason codes, independent of HRESULT/device state. Failure reporting itself never allocates.
enum { ReasonFormat=1, ReasonBudget=2, ReasonJpeg=3, ReasonImageSize=4,
	ReasonOpen=101, ReasonRead=102, ReasonAllocation=103, ReasonWic=104, ReasonBuffer=105, ReasonTexture=106, ReasonUnexpected=107 };
struct Failure { int kind, reason; const char* text; };
static void Require(bool ok,const char* text,int reason=ReasonFormat){ if(!ok)throw Failure{EvLoadError,reason,text}; }
static void Runtime(bool ok,const char* text,int reason){ if(!ok)throw Failure{EvLoadFailed,reason,text}; }
static Failure AllocationFailure(){ return {EvLoadFailed,ReasonAllocation,"allocation failed"}; }
static Failure UnexpectedFailure(){ return {EvLoadFailed,ReasonUnexpected,"unexpected native exception"}; }
// Same contract as SpatialFormat.cs. SnLimits exposes every value; the managed tests assert exact parity.
static constexpr uint64_t MaxFileBytes=1ull<<30, MaxDenseSlots=1ull<<22, MaxBlocks=1ull<<18;
static constexpr uint64_t MaxVertices=MaxBlocks*4, MaxIndices=MaxBlocks*6, MaxMeshBytes=MaxVertices*20+MaxIndices*4, MaxAtlasPixels=1ull<<26;
static constexpr uint64_t MaxValidationBytes=(MaxDenseSlots+7)/8+4096, MaxSlices=64, MaxBlockSize=256, MaxDimension=16384, HeaderBytes=68;
static uint64_t Add(uint64_t a,uint64_t b){ Require(a<=UINT64_MAX-b,"arithmetic overflow"); return a+b; }
static uint64_t Mul(uint64_t a,uint64_t b){ Require(!b||a<=UINT64_MAX/b,"arithmetic overflow"); return a*b; }
static bool Dimension(uint64_t n){ return n>=1&&n<=MaxDimension; }
static bool Positive(float f){ return std::isfinite(f)&&f>0; }
// Explicit test entry points set this only on their own worker. Normal rendering always leaves it zero.
// 1: scene allocation; 2: allocation with file open; 3: reject allocation requests >1 MiB;
// 4: WIC infrastructure; 5: unexpected exception; 6/7: buffer/texture failure; 8: WIC size mismatch.
static thread_local int testFault=0;
static thread_local uint64_t allocationBytes=0;
static void Allocate(uint64_t bytes){ if(testFault==3&&bytes>(1ull<<20))throw std::bad_alloc(); allocationBytes=Add(allocationBytes,bytes); }
struct FileHandle {
	HANDLE value=INVALID_HANDLE_VALUE;
	explicit FileHandle(HANDLE h):value(h){} ~FileHandle(){ if(value!=INVALID_HANDLE_VALUE&&value)CloseHandle(value); }
	HANDLE Release(){ HANDLE h=value; value=INVALID_HANDLE_VALUE; return h; }
	FileHandle(const FileHandle&)=delete; FileHandle& operator=(const FileHandle&)=delete;
};
struct ComApartment { HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED); ~ComApartment(){ if(SUCCEEDED(hr))CoUninitialize(); } };

struct SnStatsData { int64_t presents, sceneDraws, composites, loads, occlusions; double lastLoadMs; int swapKind; int width, height; float minZ; double benchScene, benchComposite; int64_t pauses; int paused; };

static double Now(){ static LARGE_INTEGER f={}; if(!f.QuadPart)QueryPerformanceFrequency(&f); LARGE_INTEGER c; QueryPerformanceCounter(&c); return c.QuadPart*1000.0/f.QuadPart; }

// ---- .spatial format (viewer.js: Wo / Go / Xo) ---------------------------------------------------------------
struct Header {
	uint32_t imageWidth, imageHeight, numSlices, blockSize; float focal; uint32_t atlasWidth, atlasHeight; bool opaqueOnly;
	float uvPadding; uint32_t totalCorners, totalBlocks, geometryBytes, colorBytes, alphaBytes, originalWidth, originalHeight;
	uint32_t gridWidth, gridHeight;
};
struct Slice { std::vector<float> depths; std::vector<uint32_t> blocks; std::vector<int16_t> atlasX, atlasY; };
struct Vertex { float x, y, z, u, v; };

static uint32_t U32(const uint8_t* p){ uint32_t v; memcpy(&v,p,4); return v; }
static float F32(const uint8_t* p){ float v; memcpy(&v,p,4); return v; }

static void Need(uint64_t off,uint64_t bytes,uint64_t end){ Require(off<=end&&bytes<=end-off,"truncated section"); }
static bool Bit(const uint8_t* mask,uint64_t n){ return (mask[n/8]&(1u<<(n%8)))!=0; }
static const uint8_t* ReadMask(const uint8_t* d,uint64_t end,uint64_t& off,uint64_t count,uint64_t expected){
	Require(expected<=count,"slice counts exceed mask size"); uint64_t bytes=Add(count,7)/8; Need(off,bytes,end);
	const uint8_t* mask=d+off; uint64_t set=0;
	for(uint64_t i=0;i<bytes;i++){ unsigned c=mask[i]; while(c){ set++; c&=c-1; } }
	Require(count%8==0||(mask[bytes-1]>>(count%8))==0,"mask padding bits are set");
	Require(set==expected,"mask popcount differs from slice count"); off+=bytes; return mask;
}
static void CheckHeader(Header& h,uint64_t size,size_t& co,size_t& ao){
	Require(h.numSlices>=1&&h.numSlices<=MaxSlices,"slice count out of range");
	Require(h.blockSize>=1&&h.blockSize<=MaxBlockSize,"block size out of range");
	Require(Dimension(h.imageWidth)&&Dimension(h.imageHeight),"image dimensions out of range");
	Require(Dimension(h.originalWidth)&&Dimension(h.originalHeight),"original dimensions out of range");
	Require(Dimension(h.atlasWidth)&&Dimension(h.atlasHeight),"atlas dimensions out of range");
	Require(Positive(h.focal),"focal must be finite and positive");
	Require(std::isfinite(h.uvPadding)&&h.uvPadding>=0&&h.uvPadding<=h.blockSize/2.0,"uvPadding out of range");
	h.gridWidth=h.imageWidth/h.blockSize; h.gridHeight=h.imageHeight/h.blockSize;
	uint64_t corners=Mul(Add(h.gridWidth,1),Add(h.gridHeight,1)), cells=Mul(h.gridWidth,h.gridHeight);
	Require(h.gridWidth&&h.gridHeight,"empty block grid");
	Require(Mul(Add(corners,cells),h.numSlices)<=MaxDenseSlots,"dense slot budget exceeded",ReasonBudget);
	Require(h.totalCorners>0&&h.totalCorners<=Mul(corners,h.numSlices),"totalCorners out of range");
	Require(h.totalBlocks>0&&h.totalBlocks<=Mul(cells,h.numSlices),"totalBlocks out of range");
	Require(h.totalBlocks<=MaxBlocks,"block budget exceeded",ReasonBudget);
	uint64_t vertices=Mul(h.totalBlocks,4),indices=Mul(h.totalBlocks,6),vb=Mul(vertices,sizeof(Vertex)),ib=Mul(indices,4);
	Require(vertices<=MaxVertices&&indices<=MaxIndices&&Add(vb,ib)<=MaxMeshBytes&&vb<=UINT32_MAX&&ib<=UINT32_MAX&&indices<=UINT32_MAX,"mesh budget exceeded",ReasonBudget);
	Require(Mul(h.atlasWidth,h.atlasHeight)<=MaxAtlasPixels,"atlas pixel budget exceeded",ReasonBudget);
	Require(h.atlasWidth/h.blockSize&&h.atlasHeight/h.blockSize,"empty atlas grid");
	uint64_t color=Add(HeaderBytes,h.geometryBytes),alpha=Add(color,h.colorBytes),end=Add(alpha,h.alphaBytes);
	Require(color<=size,"truncated geometry section"); Require(h.colorBytes&&h.alphaBytes,"empty image section");
	Require(end<=size,"truncated image sections"); Require(end==size,"trailing bytes after image sections"); co=(size_t)color; ao=(size_t)alpha;
}
static void CheckProjection(const Header& h,float minZ,float maxZ){
	float aspect=(float)((double)h.originalWidth/h.originalHeight);
	double tanHalf=h.originalHeight/(2.0*h.focal)*std::min(1.0,((double)h.originalWidth/h.originalHeight)/aspect);
	float scale=(float)(1/tanHalf),nearZ=std::max(1e-4f,minZ*.001f),farZ=std::max(100.f,maxZ*100.f),range=nearZ-farZ,product=nearZ*farZ;
	Require(Positive(scale)&&Positive(scale/aspect)&&std::isfinite(farZ)&&range!=0&&std::isfinite(product)&&std::isfinite(farZ/range)&&std::isfinite(product/range),"camera projection is not finite");
}
// The managed validator stops at SOFn too; WIC decoding is a separate, stricter content check.
static void CheckJpeg(const uint8_t* d,uint64_t size,uint32_t ew,uint32_t eh){
	uint64_t off=0; auto byte=[&](){ Need(off,1,size); return (unsigned)d[off++]; };
	auto word=[&](){ unsigned a=byte(); return (a<<8)|byte(); };
	Require(size>=2&&byte()==255&&byte()==216,"JPEG SOI missing",ReasonJpeg);
	while(off<size){
		Require(byte()==255,"JPEG marker missing",ReasonJpeg); unsigned marker; do{ marker=byte(); }while(marker==255);
		Require(marker!=0&&marker!=216&&marker!=217&&marker!=218&&!(marker>=208&&marker<=215),"JPEG SOF missing",ReasonJpeg);
		if(marker==1)continue; unsigned length=word(); Require(length>=2,"JPEG segment length invalid",ReasonJpeg);
		Need(off,length-2,size); uint64_t end=off+length-2;
		if(marker>=192&&marker<=207&&marker!=196&&marker!=200&&marker!=204){
			Require(length>=8,"JPEG SOF truncated",ReasonJpeg); unsigned precision=byte(),height=word(),width=word(),components=byte();
			Require(precision>0&&components>=1&&components<=4&&length==8+3*components,"JPEG SOF layout invalid",ReasonJpeg);
			Require(Dimension(width)&&Dimension(height)&&Mul(width,height)<=MaxAtlasPixels,"JPEG pixel budget exceeded",ReasonBudget);
			Require(width==ew&&height==eh,"JPEG dimensions differ from atlas",ReasonImageSize); return;
		}
		off=end;
	}
	Require(false,"JPEG SOF missing",ReasonJpeg);
}
static void Geometry(const std::vector<uint8_t>& file,const Header& h,uint64_t end,std::vector<Slice>* slices){
	const uint8_t* d=file.data(); uint64_t off=HeaderBytes,gw=h.gridWidth,corners=Mul(gw+1,(uint64_t)h.gridHeight+1),cells=Mul(gw,h.gridHeight),sumCorners=0,sumBlocks=0;
	float minZ=INFINITY,maxZ=0;
	for(uint32_t s=0;s<h.numSlices;s++){
		Need(off,8,end); uint64_t nd=U32(d+off),nb=U32(d+off+4); off+=8;
		Require(nd<=corners&&nb<=cells,"slice counts exceed mask size"); sumCorners=Add(sumCorners,nd); sumBlocks=Add(sumBlocks,nb);
		Require(sumCorners<=h.totalCorners&&sumBlocks<=h.totalBlocks,"slice sums exceed header totals");
		const uint8_t* cm=ReadMask(d,end,off,corners,nd); Need(off,Mul(nd,4),end);
		Slice* sl=slices?&(*slices)[s]:nullptr;
		if(sl){ Allocate(Mul(corners,4)); sl->depths.assign((size_t)corners,0.f); }
		for(uint64_t n=0;n<corners;n++)if(Bit(cm,n)){
			float z=F32(d+off); off+=4; Require(Positive(z),"depth must be finite and positive"); minZ=std::min(minZ,z); maxZ=std::max(maxZ,z);
			float scaled=(float)(std::max(h.imageWidth,h.imageHeight)*.5)*z;
			Require(std::isfinite(scaled)&&std::isfinite(scaled/h.focal),"mesh projection is not finite"); if(sl)sl->depths[(size_t)n]=z;
		}
		const uint8_t* bm=ReadMask(d,end,off,cells,nb); Need(off,Mul(nb,2),end);
		if(sl){ Allocate(Mul(cells,4)); sl->atlasX.assign((size_t)cells,-1); sl->atlasY.assign((size_t)cells,-1); Allocate(Mul(nb,4)); sl->blocks.reserve((size_t)nb); }
		for(uint64_t n=0;n<cells;n++)if(Bit(bm,n)){
			uint64_t c=(n/gw)*(gw+1)+n%gw;
			Require(Bit(cm,c)&&Bit(cm,c+1)&&Bit(cm,c+gw+1)&&Bit(cm,c+gw+2),"block is missing depth corners");
			unsigned x=d[off++],y=d[off++]; Require(x<h.atlasWidth/h.blockSize&&y<h.atlasHeight/h.blockSize,"atlas coordinate out of range");
			if(sl){ sl->blocks.push_back((uint32_t)n); sl->atlasX[(size_t)n]=(int16_t)x; sl->atlasY[(size_t)n]=(int16_t)y; }
		}
	}
	Require(sumCorners==h.totalCorners&&sumBlocks==h.totalBlocks,"slice sums differ from header totals");
	Require(off==end,"geometry end differs from geometryBytes"); CheckProjection(h,minZ,maxZ);
}
static void AllocateSlices(std::vector<Slice>& slices,uint32_t count){ Allocate(Mul(count,sizeof(Slice))); slices.resize(count); }
static void ParseSpatial(const std::vector<uint8_t>& file,Header& h,std::vector<Slice>& slices,size_t& colorOff,size_t& alphaOff){
	const uint8_t* d=file.data(); uint64_t size=file.size();
	Require(size<=MaxFileBytes,"file exceeds 1 GiB cap",ReasonBudget);
	Require(size>=HeaderBytes,"truncated header"); Require(U32(d)==4280403,"not a Spatial file");
	h.imageWidth=U32(d+4); h.imageHeight=U32(d+8); h.numSlices=U32(d+12); h.blockSize=U32(d+16); h.focal=F32(d+20);
	h.atlasWidth=U32(d+24); h.atlasHeight=U32(d+28); h.opaqueOnly=(U32(d+32)&1)!=0; h.uvPadding=F32(d+36);
	h.totalCorners=U32(d+40); h.totalBlocks=U32(d+44); h.geometryBytes=U32(d+48); h.colorBytes=U32(d+52); h.alphaBytes=U32(d+56);
	h.originalWidth=U32(d+60); h.originalHeight=U32(d+64);
	CheckHeader(h,size,colorOff,alphaOff);
	Geometry(file,h,colorOff,nullptr); // Complete allocation-free preflight; no untrusted count reaches a vector.
	CheckJpeg(d+colorOff,h.colorBytes,h.atlasWidth,h.atlasHeight); CheckJpeg(d+alphaOff,h.alphaBytes,h.atlasWidth,h.atlasHeight);
	AllocateSlices(slices,h.numSlices); Geometry(file,h,colorOff,&slices);
}

// viewer.js Xo: one quad per block, back slices first (the draw order is the blend order), float32 math as Math.fround.
static void BuildMesh(const Header& t,const std::vector<Slice>& slices,std::vector<Vertex>& verts,std::vector<uint32_t>& indices,float& minZ,float& maxZ){
	verts.clear(); indices.clear(); minZ=INFINITY; maxZ=-INFINITY;
	uint64_t blocks=0; Require(slices.size()==t.numSlices,"mesh slice count differs");
	for(const auto& s:slices)blocks=Add(blocks,s.blocks.size());
	Require(blocks==t.totalBlocks&&blocks>0&&blocks<=MaxBlocks,"mesh block budget exceeded",ReasonBudget);
	uint64_t nv=Mul(blocks,4),ni=Mul(blocks,6),vb=Mul(nv,sizeof(Vertex)),ib=Mul(ni,4);
	Require(nv<=MaxVertices&&ni<=MaxIndices&&Add(vb,ib)<=MaxMeshBytes&&vb<=UINT32_MAX&&ib<=UINT32_MAX,"mesh budget exceeded",ReasonBudget);
	Allocate(vb); verts.reserve((size_t)nv); Allocate(ib); indices.reserve((size_t)ni);
	auto a=[&](double l,float c,float h)->float{ float d=(float)(l-(double)c); d=d*h; return d/t.focal; };
	int gw=(int)t.gridWidth, gh=(int)t.gridHeight; float bs=(float)t.blockSize;
	for(int l=(int)slices.size()-1;l>=0;l--){
		const Slice& s=slices[l];
		auto m=[&](int g,int x,int p){ return s.atlasX[g]==x&&s.atlasY[g]==p; };
		for(uint32_t gu:s.blocks){
			Require(gu<(uint64_t)gw*gh,"mesh block out of range"); int g=(int)gu;
			int x=g%gw, p=g/gw, u=s.atlasX[g], E=s.atlasY[g];
			float v=(float)(u*t.blockSize), w=(float)((u+1)*t.blockSize), P=(float)(E*t.blockSize), R=(float)((E+1)*t.blockSize);
			if(!(x>0&&m(g-1,u-1,E)))v=v+t.uvPadding;
			if(!(x+1<gw&&m(g+1,u+1,E)))w=w-t.uvPadding;
			if(!(p>0&&m(g-gw,u,E-1)))P=P+t.uvPadding;
			if(!(p+1<gh&&m(g+gw,u,E+1)))R=R-t.uvPadding;
			int T[4]={x,x+1,x+1,x}, q[4]={p+1,p+1,p,p}; float M[4]={v,w,w,v}, b[4]={R,R,P,P};
			uint32_t z=(uint32_t)verts.size();
			for(int W=0;W<4;W++){
				float nt=s.depths[q[W]*(gw+1)+T[W]];
				Vertex vx; vx.x=a((double)(T[W]*bs),(float)(t.imageWidth*.5),nt); vx.y=a(t.imageHeight*.5,(float)(q[W]*bs),nt); vx.z=nt;
				vx.u=(float)((double)M[W]/t.atlasWidth); vx.v=(float)((double)b[W]/t.atlasHeight);
				Require(std::isfinite(vx.x)&&std::isfinite(vx.y)&&Positive(vx.z)&&std::isfinite(vx.u)&&std::isfinite(vx.v),"mesh vertex is not finite");
				verts.push_back(vx); if(nt<minZ)minZ=nt; if(nt>maxZ)maxZ=nt;
			}
			uint32_t q6[6]={z,z+1,z+2,z,z+2,z+3}; indices.insert(indices.end(),q6,q6+6);
		}
	}
}

// ---- images --------------------------------------------------------------------------------------------------
static void WicResult(HRESULT hr,const char* stage){
	if(SUCCEEDED(hr))return;
	if(hr==E_OUTOFMEMORY||hr==HRESULT_FROM_WIN32(ERROR_NOT_ENOUGH_MEMORY))throw AllocationFailure();
	bool content=hr==WINCODEC_ERR_BADIMAGE||hr==WINCODEC_ERR_BADHEADER||hr==WINCODEC_ERR_FRAMEMISSING||hr==WINCODEC_ERR_BADMETADATAHEADER||
		hr==WINCODEC_ERR_UNKNOWNIMAGEFORMAT||hr==WINCODEC_ERR_BADSTREAMDATA||hr==WINCODEC_ERR_STREAMREAD||hr==WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT||
		hr==WINCODEC_ERR_VALUEOUTOFRANGE||hr==WINCODEC_ERR_TOOMUCHMETADATA||hr==WINCODEC_ERR_IMAGESIZEOUTOFRANGE;
	throw Failure{content?EvLoadError:EvLoadFailed,content?ReasonJpeg:ReasonWic,stage};
}
static ComPtr<IWICImagingFactory> MakeWic(){
	Runtime(testFault!=4,"WIC infrastructure unavailable",ReasonWic); ComPtr<IWICImagingFactory> wic;
	HRESULT hr=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wic));
	if(hr==E_OUTOFMEMORY)throw AllocationFailure(); Runtime(SUCCEEDED(hr)&&wic,"WIC factory unavailable",ReasonWic); return wic;
}
static ComPtr<IWICBitmapFrameDecode> OpenJpeg(IWICImagingFactory* wic,const uint8_t* data,size_t size,UINT ew,UINT eh){
	Runtime(wic!=nullptr,"WIC factory unavailable",ReasonWic); Require(size<=MaxFileBytes,"image size exceeds cap",ReasonBudget);
	ComPtr<IWICStream> stream; WicResult(wic->CreateStream(&stream),"WIC stream creation failed");
	WicResult(stream->InitializeFromMemory(const_cast<BYTE*>(data),(DWORD)size),"WIC memory stream failed");
	ComPtr<IWICBitmapDecoder> dec; WicResult(wic->CreateDecoderFromStream(stream.Get(),nullptr,WICDecodeMetadataCacheOnDemand,&dec),"JPEG decoder failed");
	ComPtr<IWICBitmapFrameDecode> frame; WicResult(dec->GetFrame(0,&frame),"JPEG frame failed");
	UINT w,h; WicResult(frame->GetSize(&w,&h),"JPEG size unavailable");
	if(testFault==8)w++; // Verify the WIC check itself, independently of the SOFn preflight.
	Require(Dimension(w)&&Dimension(h)&&Mul(w,h)<=MaxAtlasPixels,"decoded image exceeds pixel budget",ReasonBudget);
	Require(w==ew&&h==eh,"decoded image dimensions differ from atlas",ReasonImageSize); return frame;
}
static ComPtr<IWICFormatConverter> Rgba(IWICImagingFactory* wic,IWICBitmapFrameDecode* frame){
	ComPtr<IWICFormatConverter> conv; WicResult(wic->CreateFormatConverter(&conv),"WIC converter creation failed");
	WicResult(conv->Initialize(frame,GUID_WICPixelFormat32bppRGBA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom),"JPEG pixel conversion failed"); return conv;
}
static void DecodeJpeg(IWICImagingFactory* wic,const uint8_t* data,size_t size,std::vector<uint8_t>& out,UINT w,UINT h){
	auto frame=OpenJpeg(wic,data,size,w,h); WICPixelFormatGUID src; WicResult(frame->GetPixelFormat(&src),"JPEG pixel format unavailable");
	Allocate(Mul(w,h)); out.resize((size_t)w*h);
	if(src==GUID_WICPixelFormat8bppGray){ WicResult(frame->CopyPixels(nullptr,w,(UINT)out.size(),out.data()),"JPEG alpha decode failed"); return; }
	auto conv=Rgba(wic,frame.Get()); Allocate(Mul(w,4)); std::vector<uint8_t> row((size_t)w*4);
	// A colour alpha image: viewer.js samples its green channel.
	for(UINT y=0;y<h;y++){ WICRect r={0,(INT)y,(INT)w,1}; WicResult(conv->CopyPixels(&r,w*4,(UINT)row.size(),row.data()),"JPEG alpha decode failed"); for(UINT x=0;x<w;x++)out[(size_t)y*w+x]=row[x*4+1]; }
}
// No D3D and no full decoded image: exercise every scanline, including corrupt entropy data after SOFn.
static void DecodeJpegBounded(IWICImagingFactory* wic,const uint8_t* data,size_t size,UINT w,UINT h){
	auto frame=OpenJpeg(wic,data,size,w,h); auto conv=Rgba(wic,frame.Get()); UINT rows=std::min(256u,h);
	uint64_t bytes=Mul(Mul(w,4),rows); Allocate(bytes); std::vector<uint8_t> strip((size_t)bytes);
	for(UINT y=0;y<h;y+=rows){ WICRect rect={0,(INT)y,(INT)w,(INT)std::min(rows,h-y)}; WicResult(conv->CopyPixels(&rect,w*4,(UINT)strip.size(),strip.data()),"JPEG pixel decode failed"); }
}

// The colour atlas (up to ~5k x 5k) is decoded in strips straight into the texture, so no full-size copy is held in memory.
// ctx: the immediate context, multithread-protected (Renderer::Init), so the loader thread can upload in strips.
static void DecodeJpegToTexture(IWICImagingFactory* wic,ID3D11Device* dev,ID3D11DeviceContext* ctx,const uint8_t* data,size_t size,UINT w,UINT h,ComPtr<ID3D11ShaderResourceView>& srv){
	auto frame=OpenJpeg(wic,data,size,w,h); auto conv=Rgba(wic,frame.Get());
	D3D11_TEXTURE2D_DESC td={}; td.Width=w; td.Height=h; td.MipLevels=1; td.ArraySize=1; td.Format=DXGI_FORMAT_R8G8B8A8_UNORM_SRGB; td.SampleDesc.Count=1;
	td.Usage=D3D11_USAGE_DEFAULT; td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
	ComPtr<ID3D11Texture2D> tex; Runtime(SUCCEEDED(dev->CreateTexture2D(&td,nullptr,&tex)),"color texture creation failed",ReasonTexture);
	const UINT strip=256; Allocate(Mul(Mul(w,4),strip)); std::vector<uint8_t> buf((size_t)w*4*strip);
	for(UINT y=0;y<h;y+=strip){
		UINT n=std::min(strip,h-y); WICRect r={0,(INT)y,(INT)w,(INT)n};
		WicResult(conv->CopyPixels(&r,w*4,w*4*n,buf.data()),"JPEG color decode failed");
		D3D11_BOX box={0,y,0,w,y+n,1}; ctx->UpdateSubresource(tex.Get(),0,&box,buf.data(),w*4,0);
	}
	Runtime(SUCCEEDED(dev->CreateShaderResourceView(tex.Get(),nullptr,&srv)),"color texture view failed",ReasonTexture);
}

static void ReadSpatialFile(const wchar_t* path,std::vector<uint8_t>& file){
	FileHandle f(CreateFileW(path,GENERIC_READ,FILE_SHARE_READ|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_FLAG_SEQUENTIAL_SCAN,nullptr));
	Runtime(f.value!=INVALID_HANDLE_VALUE,"file open failed",ReasonOpen);
	LARGE_INTEGER size={}; Runtime(GetFileSizeEx(f.value,&size)!=FALSE&&size.QuadPart>=0,"file size read failed",ReasonRead);
	Require((uint64_t)size.QuadPart<=MaxFileBytes,"file exceeds 1 GiB cap",ReasonBudget); Require(size.QuadPart>=HeaderBytes,"truncated header");
	if(testFault==2)throw std::bad_alloc(); if(testFault==5)throw std::runtime_error("injected failure");
	Allocate((uint64_t)size.QuadPart); file.resize((size_t)size.QuadPart); uint64_t off=0;
	while(off<(uint64_t)size.QuadPart){ DWORD want=(DWORD)std::min(1ull<<20,(uint64_t)size.QuadPart-off),got=0;
		Runtime(testFault!=9,"file read failed",ReasonRead);
		Runtime(ReadFile(f.value,file.data()+(size_t)off,want,&got,nullptr)!=FALSE&&got>0,"file read failed",ReasonRead); off+=got;
	}
}
static void PrepareSpatial(const wchar_t* path,std::vector<uint8_t>& file,Header& h,std::vector<Vertex>& verts,std::vector<uint32_t>& idx,float& minZ,float& maxZ,size_t& co,size_t& ao){
	ReadSpatialFile(path,file); std::vector<Slice> slices; ParseSpatial(file,h,slices,co,ao); BuildMesh(h,slices,verts,idx,minZ,maxZ);
}

static HRESULT MakeTexture(ID3D11Device* dev,DXGI_FORMAT fmt,UINT w,UINT h,const void* data,UINT pitch,ComPtr<ID3D11ShaderResourceView>& srv){
	D3D11_TEXTURE2D_DESC td={}; td.Width=w; td.Height=h; td.MipLevels=1; td.ArraySize=1; td.Format=fmt; td.SampleDesc.Count=1;
	td.Usage=D3D11_USAGE_IMMUTABLE; td.BindFlags=D3D11_BIND_SHADER_RESOURCE;
	D3D11_SUBRESOURCE_DATA sd={data,pitch,0}; ComPtr<ID3D11Texture2D> tex;
	HRESULT hr=dev->CreateTexture2D(&td,&sd,&tex); if(FAILED(hr))return hr;
	return dev->CreateShaderResourceView(tex.Get(),nullptr,&srv);
}

// ---- scenes ---------------------------------------------------------------------------------------------------
struct Target { ComPtr<ID3D11Texture2D> tex; ComPtr<ID3D11RenderTargetView> rtv; ComPtr<ID3D11ShaderResourceView> srv; };
struct Scene {
	Header h; ComPtr<ID3D11Buffer> vb, ib; UINT indexCount=0; ComPtr<ID3D11ShaderResourceView> color, alpha; float minZ=1, maxZ=1;
	float bg[3]={0,0,0}; float camX=0, camY=0; int sequence=0;
	ComPtr<ID3D11ShaderResourceView> backdrop;   // the ambient background (window-sized), drawn instead of the colour
	Target rt; bool rtValid=false; float rtCamX=0, rtCamY=0;   // offscreen copy, only while it is part of a cross-fade
	float drawnX=NAN, drawnY=NAN;
};
// One thing on screen: a scene, the flat still (fade in/out), or neither (background colour only).
struct Layer { std::shared_ptr<Scene> scene; bool still=false; bool Empty()const{ return !scene&&!still; } };

static const char* Shaders=R"(
cbuffer SceneCB : register(b0) { float2 cam; float2 scale; float zA; float zB; float2 pad0; };
cbuffer MixCB : register(b1) { float mixv; float3 pad1; };
Texture2D tex0 : register(t0); Texture2D tex1 : register(t1); SamplerState samp : register(s0);
struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD0; };
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
// three.js: mesh rotated by PI about Y (x,z negated), camera at (cam,0) without rotation, PerspectiveCamera projection.
VSOut SceneVS(VSIn i){ VSOut o; float xe=-i.pos.x-cam.x, ye=i.pos.y-cam.y, ze=-i.pos.z; o.pos=float4(scale.x*xe,scale.y*ye,zA*ze+zB,-ze); o.uv=i.uv; return o; }
// three.js sRGBTransferOETF (the canvas is not an sRGB framebuffer; colour is encoded in the shader, blended encoded)
float3 Encode(float3 v){ return lerp(pow(v,0.41666)*1.055-0.055, v*12.92, step(v,0.0031308)); }
// MeshBasicMaterial: map (sRGB texture) * alphaMap.g, premultipliedAlpha
// (a fully transparent fragment adds nothing to the blend: skipped, to save the blend unit's bandwidth)
float4 ScenePS(VSOut i):SV_Target{ float a=tex1.Sample(samp,i.uv).r; if(a<=0)discard; float4 c=tex0.Sample(samp,i.uv); a*=c.a; return float4(Encode(c.rgb)*a,a); }
float4 OpaquePS(VSOut i):SV_Target{ float4 c=tex0.Sample(samp,i.uv); float a=c.a*tex1.Sample(samp,i.uv).r; if(a<0.5)discard; return float4(Encode(c.rgb),1); }
float4 FullVS(uint id:SV_VertexID):SV_Position{ float2 p=float2((id<<1)&2,id&2); return float4(p*float2(2,-2)+float2(-1,1),0,1); }
float4 CompositePS(float4 pos:SV_Position):SV_Target{ int3 p=int3(pos.xy,0); return lerp(tex0.Load(p),tex1.Load(p),mixv); }
)";

struct SceneCB { float cam[2]; float scale[2]; float zA, zB; float pad[2]; };
struct MixCB { float mix; float pad[3]; };

static double Smoothstep(double t){ return t*t*(3-2*t); }
// CSS 'ease-in-out' = cubic-bezier(.42,0,.58,1)
static double EaseInOut(double x){
	if(x<=0)return 0; if(x>=1)return 1;
	auto bx=[](double t){ return 3*(1-t)*(1-t)*t*.42+3*(1-t)*t*t*.58+t*t*t; };
	auto by=[](double t){ return 3*(1-t)*t*t+t*t*t; };
	double lo=0,hi=1,t=x; for(int i=0;i<40;i++){ t=(lo+hi)/2; if(bx(t)<x)lo=t; else hi=t; }
	return by(t);
}

struct Renderer {
	HWND hwnd=nullptr; int width=0, height=0; SnCallback callback=nullptr;
	ComPtr<ID3D11Device> dev; ComPtr<ID3D11DeviceContext> ctx; ComPtr<IDXGISwapChain2> swap; HANDLE waitable=nullptr; int swapKind=0;
	ComPtr<IDCompositionDevice> dcomp; ComPtr<IDCompositionTarget> dcompTarget; ComPtr<IDCompositionVisual> dcompVisual;
	ComPtr<ID3D11VertexShader> sceneVS, fullVS; ComPtr<ID3D11PixelShader> scenePS, opaquePS, compositePS; ComPtr<ID3D11InputLayout> layout;
	ComPtr<ID3D11Buffer> sceneCB, mixCB; ComPtr<ID3D11SamplerState> sampler; ComPtr<ID3D11BlendState> premul;
	ComPtr<ID3D11DepthStencilState> noDepth, lessEqual; ComPtr<ID3D11RasterizerState> raster, rasterFull;
	ComPtr<ID3D11Texture2D> depthTex; ComPtr<ID3D11DepthStencilView> dsv;
	ComPtr<ID3D11ShaderResourceView> stillSrv;
	float sensitivity=0.03f, snappiness=4.f;

	// render-thread state
	Layer current, from, to; bool fading=false; double fadeStart=0, fadeMs=0; int fadeKind=0; bool needDraw=true;
	bool fadeOutStarted=false, fadeOutDonePending=false; bool occluded=false; double occludedTest=0;
	float bgNow[3]={0,0,0};

	// shared with the API / loader threads
	std::mutex lock; HANDLE wake=nullptr, firstPresent=nullptr; std::atomic<bool> quit{false};
	// SnSetPaused: the desktop is not visible (fullscreen app, screen off, locked). The pointer no longer drives redraws
	// and the thread sleeps between commands; see RenderRun.
	std::atomic<bool> paused{false};
	std::shared_ptr<Scene> ready; int latestSequence=0; float pendingBg[3]={0,0,0}; bool colorChanged=false;
	ComPtr<ID3D11ShaderResourceView> pendingBackdrop;   // SnSetBackdrop: given to the scenes loaded after it
	std::vector<uint8_t> stillPixels; int stillW=0, stillH=0; bool stillNew=false; int fadeOutMs=-1; std::atomic<int> stillFadeMs{700};
	bool pointerFixed=false, pointerCircle=false; float fixedX=0, fixedY=0; bool settle=false;
	// SnSetMotion: 0 = follow the cursor, 1 = the opposite way, 2 = drift on its own (no cursor): a slow loop around an
	// ellipse in the screen's proportions, so the whole depth of the scene comes round in turn. Drift draws all the time,
	// so it presents every presentInterval-th refresh (60 fps) instead of every one.
	// 3 = handheld: the camera wanders a little all the time, as if held in a hand (Pointer); like drift it draws every
	// presentInterval-th refresh.
	std::atomic<int> motion{0}; int presentInterval=1;
	static constexpr double DriftPeriodMs=32000, DriftRadius=.8;
	std::wstring capturePath; HANDLE captureDone=nullptr; HRESULT captureResult=S_OK; float captureMix=-1;
	SnStatsData stats={};

	std::mutex loadLock; std::condition_variable loadCv; std::wstring loadPath; int loadSequence=0; bool loadPending=false;
	std::thread renderThread, loaderThread;
	struct LoadError { int sequence=0; Failure failure={}; };
	std::mutex errorLock; LoadError loadErrors[64]={}; unsigned errorNext=0;
	bool testOnly=false; int loaderFault=0; // Only SnTestLoader creates this headless, one-request renderer.

	void Emit(int kind,int seq)noexcept{ try{ if(callback)callback(kind,seq); }catch(...){} }
	void Report(int seq,Failure f){ { std::lock_guard<std::mutex> g(errorLock); auto& e=loadErrors[errorNext++%64]; e.sequence=seq; e.failure=f; } if(!quit)Emit(f.kind,seq); }

	HRESULT Init(std::string& err){
		UINT flags=D3D11_CREATE_DEVICE_BGRA_SUPPORT; D3D_FEATURE_LEVEL levels[]={D3D_FEATURE_LEVEL_11_1,D3D_FEATURE_LEVEL_11_0,D3D_FEATURE_LEVEL_10_1,D3D_FEATURE_LEVEL_10_0};
		HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,flags,levels,4,D3D11_SDK_VERSION,&dev,nullptr,&ctx);
		if(FAILED(hr)){ err="D3D11CreateDevice"; return hr; }
		{ ComPtr<ID3D11Multithread> mt; if(SUCCEEDED(ctx.As(&mt)))mt->SetMultithreadProtected(TRUE); }   // the loader uploads in strips
		ComPtr<IDXGIDevice> dxgiDev; dev.As(&dxgiDev); ComPtr<IDXGIAdapter> adapter; dxgiDev->GetAdapter(&adapter);
		ComPtr<IDXGIFactory2> factory; hr=adapter->GetParent(IID_PPV_ARGS(&factory)); if(FAILED(hr)){ err="IDXGIFactory2"; return hr; }
		DXGI_SWAP_CHAIN_DESC1 sd={}; sd.Width=width; sd.Height=height; sd.Format=DXGI_FORMAT_B8G8R8A8_UNORM; sd.SampleDesc.Count=1;
		sd.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT; sd.BufferCount=3; sd.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;
		sd.AlphaMode=DXGI_ALPHA_MODE_IGNORE; sd.Flags=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT; sd.Scaling=DXGI_SCALING_NONE;
		ComPtr<IDXGISwapChain1> sc1;
		wchar_t mode[32]={}; GetEnvironmentVariableW(L"SPATIAL_SWAP",mode,32);   // tests: "dcomp" forces the DirectComposition path
		hr=wcscmp(mode,L"dcomp")==0?E_NOTIMPL:factory->CreateSwapChainForHwnd(dev.Get(),hwnd,&sd,nullptr,nullptr,&sc1);
		if(SUCCEEDED(hr))swapKind=1;
		else {
			// e.g. a window the flip model will not take: hand the same kind of swap chain to DirectComposition instead
			sd.Scaling=DXGI_SCALING_STRETCH;
			hr=factory->CreateSwapChainForComposition(dev.Get(),&sd,nullptr,&sc1); if(FAILED(hr)){ err="CreateSwapChain"; return hr; }
			hr=DCompositionCreateDevice(dxgiDev.Get(),IID_PPV_ARGS(&dcomp)); if(FAILED(hr)){ err="DComp"; return hr; }
			hr=dcomp->CreateTargetForHwnd(hwnd,TRUE,&dcompTarget); if(FAILED(hr)){ err="DComp target"; return hr; }
			dcomp->CreateVisual(&dcompVisual); dcompVisual->SetContent(sc1.Get()); dcompTarget->SetRoot(dcompVisual.Get()); dcomp->Commit();
			swapKind=2;
		}
		factory->MakeWindowAssociation(hwnd,DXGI_MWA_NO_ALT_ENTER|DXGI_MWA_NO_WINDOW_CHANGES);
		hr=sc1.As(&swap); if(FAILED(hr)){ err="IDXGISwapChain2"; return hr; }
		// Two frames in flight: a scene takes longer than one 240 Hz refresh on this iGPU, and with one frame queued every
		// such frame would wait out a whole extra refresh (half the frame rate).
		swap->SetMaximumFrameLatency(2); waitable=swap->GetFrameLatencyWaitableObject();

		ComPtr<ID3DBlob> blob,errors; auto compile=[&](const char* entry,const char* target)->HRESULT{
			blob.Reset(); errors.Reset();
			HRESULT r=D3DCompile(Shaders,strlen(Shaders),"spatial",nullptr,nullptr,entry,target,D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&blob,&errors);
			if(FAILED(r)){ err=std::string("shader ")+entry+": "+(errors?(const char*)errors->GetBufferPointer():""); } return r; };
		if(FAILED(hr=compile("SceneVS","vs_4_0")))return hr; dev->CreateVertexShader(blob->GetBufferPointer(),blob->GetBufferSize(),nullptr,&sceneVS);
		D3D11_INPUT_ELEMENT_DESC il[]={{"POSITION",0,DXGI_FORMAT_R32G32B32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},{"TEXCOORD",0,DXGI_FORMAT_R32G32_FLOAT,0,12,D3D11_INPUT_PER_VERTEX_DATA,0}};
		if(FAILED(hr=dev->CreateInputLayout(il,2,blob->GetBufferPointer(),blob->GetBufferSize(),&layout))){ err="input layout"; return hr; }
		if(FAILED(hr=compile("ScenePS","ps_4_0")))return hr; dev->CreatePixelShader(blob->GetBufferPointer(),blob->GetBufferSize(),nullptr,&scenePS);
		if(FAILED(hr=compile("OpaquePS","ps_4_0")))return hr; dev->CreatePixelShader(blob->GetBufferPointer(),blob->GetBufferSize(),nullptr,&opaquePS);
		if(FAILED(hr=compile("FullVS","vs_4_0")))return hr; dev->CreateVertexShader(blob->GetBufferPointer(),blob->GetBufferSize(),nullptr,&fullVS);
		if(FAILED(hr=compile("CompositePS","ps_4_0")))return hr; dev->CreatePixelShader(blob->GetBufferPointer(),blob->GetBufferSize(),nullptr,&compositePS);

		D3D11_BUFFER_DESC bd={}; bd.ByteWidth=sizeof(SceneCB); bd.Usage=D3D11_USAGE_DEFAULT; bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
		dev->CreateBuffer(&bd,nullptr,&sceneCB); bd.ByteWidth=sizeof(MixCB); dev->CreateBuffer(&bd,nullptr,&mixCB);
		D3D11_SAMPLER_DESC smp={}; smp.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR; smp.AddressU=smp.AddressV=smp.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP; smp.MaxLOD=D3D11_FLOAT32_MAX;
		dev->CreateSamplerState(&smp,&sampler);
		D3D11_BLEND_DESC bl={}; auto& rt=bl.RenderTarget[0]; rt.BlendEnable=TRUE; rt.SrcBlend=D3D11_BLEND_ONE; rt.DestBlend=D3D11_BLEND_INV_SRC_ALPHA; rt.BlendOp=D3D11_BLEND_OP_ADD;
		rt.SrcBlendAlpha=D3D11_BLEND_ONE; rt.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA; rt.BlendOpAlpha=D3D11_BLEND_OP_ADD; rt.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
		dev->CreateBlendState(&bl,&premul);
		D3D11_DEPTH_STENCIL_DESC ds={}; ds.DepthEnable=FALSE; dev->CreateDepthStencilState(&ds,&noDepth);
		ds.DepthEnable=TRUE; ds.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL; ds.DepthFunc=D3D11_COMPARISON_LESS_EQUAL; dev->CreateDepthStencilState(&ds,&lessEqual);
		// material side BackSide: three culls the faces that are counter-clockwise on screen
		D3D11_RASTERIZER_DESC rs={}; rs.FillMode=D3D11_FILL_SOLID; rs.CullMode=D3D11_CULL_FRONT; rs.FrontCounterClockwise=TRUE; rs.DepthClipEnable=TRUE; rs.ScissorEnable=TRUE;
		dev->CreateRasterizerState(&rs,&raster); rs.CullMode=D3D11_CULL_NONE; rs.ScissorEnable=FALSE; dev->CreateRasterizerState(&rs,&rasterFull);
		return S_OK;
	}

	HRESULT MakeTarget(Target& t){
		if(t.tex)return S_OK;
		D3D11_TEXTURE2D_DESC td={}; td.Width=width; td.Height=height; td.MipLevels=1; td.ArraySize=1; td.Format=DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count=1;
		td.Usage=D3D11_USAGE_DEFAULT; td.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
		HRESULT hr=dev->CreateTexture2D(&td,nullptr,&t.tex); if(FAILED(hr))return hr;
		dev->CreateRenderTargetView(t.tex.Get(),nullptr,&t.rtv); return dev->CreateShaderResourceView(t.tex.Get(),nullptr,&t.srv);
	}

	// Contain fit (viewer.js resize): the photo's aspect, centred; the camera's vertical fov covers originalHeight.
	void SceneViewport(const Scene& s,D3D11_VIEWPORT& vp,D3D11_RECT& sc,float& aspect){
		double t=width, e=height, n=t, sH=e, l=(double)s.h.originalWidth/s.h.originalHeight;
		if(t/e>l)n=e*l; else sH=t/l;
		double o=(t-n)/2, a=(e-sH)/2;
		// three.js floors the GL viewport, whose y counts from the bottom
		double x0=std::floor(o), w=std::floor(n), yb=std::floor(a), h=std::floor(sH);
		vp.TopLeftX=(float)x0; vp.TopLeftY=(float)(e-(yb+h)); vp.Width=(float)w; vp.Height=(float)h; vp.MinDepth=0; vp.MaxDepth=1;
		sc.left=(LONG)vp.TopLeftX; sc.top=(LONG)vp.TopLeftY; sc.right=sc.left+(LONG)vp.Width; sc.bottom=sc.top+(LONG)vp.Height;
		aspect=(float)(n/sH);
	}

	void DrawScene(Scene& s,ID3D11RenderTargetView* rtv){
		if(s.backdrop)Composite(s.backdrop.Get(),s.backdrop.Get(),0,rtv);
		else { float clear[4]={s.bg[0],s.bg[1],s.bg[2],1}; ctx->ClearRenderTargetView(rtv,clear); }
		D3D11_VIEWPORT vp; D3D11_RECT sc; float aspect; SceneViewport(s,vp,sc,aspect);
		double l=(double)s.h.originalWidth/s.h.originalHeight;
		double tanHalf=s.h.originalHeight/(2.0*s.h.focal)*std::min(1.0,l/aspect);
		float nearZ=std::max(1e-4f,s.minZ*.001f), farZ=std::max(100.f,s.maxZ*100.f);
		SceneCB cb={}; cb.cam[0]=s.camX; cb.cam[1]=s.camY; cb.scale[1]=(float)(1/tanHalf); cb.scale[0]=cb.scale[1]/aspect;
		cb.zA=farZ/(nearZ-farZ); cb.zB=nearZ*farZ/(nearZ-farZ);
		ctx->UpdateSubresource(sceneCB.Get(),0,nullptr,&cb,0,0);
		bool opaque=s.h.opaqueOnly;
		if(opaque){
			if(!dsv){ D3D11_TEXTURE2D_DESC td={}; td.Width=width; td.Height=height; td.MipLevels=1; td.ArraySize=1; td.Format=DXGI_FORMAT_D32_FLOAT; td.SampleDesc.Count=1; td.BindFlags=D3D11_BIND_DEPTH_STENCIL;
				dev->CreateTexture2D(&td,nullptr,&depthTex); dev->CreateDepthStencilView(depthTex.Get(),nullptr,&dsv); }
			ctx->ClearDepthStencilView(dsv.Get(),D3D11_CLEAR_DEPTH,1,0);
		}
		ctx->OMSetRenderTargets(1,&rtv,opaque?dsv.Get():nullptr);
		ctx->OMSetBlendState(opaque?nullptr:premul.Get(),nullptr,0xffffffff);
		ctx->OMSetDepthStencilState(opaque?lessEqual.Get():noDepth.Get(),0);
		ctx->RSSetState(raster.Get()); ctx->RSSetViewports(1,&vp); ctx->RSSetScissorRects(1,&sc);
		UINT stride=sizeof(Vertex), offset=0; ctx->IASetVertexBuffers(0,1,s.vb.GetAddressOf(),&stride,&offset);
		ctx->IASetIndexBuffer(s.ib.Get(),DXGI_FORMAT_R32_UINT,0); ctx->IASetInputLayout(layout.Get()); ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		ctx->VSSetShader(sceneVS.Get(),nullptr,0); ctx->VSSetConstantBuffers(0,1,sceneCB.GetAddressOf());
		ctx->PSSetShader(opaque?opaquePS.Get():scenePS.Get(),nullptr,0);
		ID3D11ShaderResourceView* srvs[2]={s.color.Get(),s.alpha.Get()}; ctx->PSSetShaderResources(0,2,srvs); ctx->PSSetSamplers(0,1,sampler.GetAddressOf());
		ctx->DrawIndexed(s.indexCount,0,0);
		ID3D11ShaderResourceView* none[2]={}; ctx->PSSetShaderResources(0,2,none); ctx->OMSetRenderTargets(0,nullptr,nullptr);
		s.drawnX=s.camX; s.drawnY=s.camY; stats.sceneDraws++;
	}

	void Composite(ID3D11ShaderResourceView* a,ID3D11ShaderResourceView* b,float mix,ID3D11RenderTargetView* rtv){
		MixCB cb={mix}; ctx->UpdateSubresource(mixCB.Get(),0,nullptr,&cb,0,0);
		D3D11_VIEWPORT vp={0,0,(float)width,(float)height,0,1};
		ctx->OMSetRenderTargets(1,&rtv,nullptr); ctx->OMSetBlendState(nullptr,nullptr,0xffffffff); ctx->OMSetDepthStencilState(noDepth.Get(),0);
		ctx->RSSetState(rasterFull.Get()); ctx->RSSetViewports(1,&vp);
		ctx->IASetInputLayout(nullptr); ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
		ctx->VSSetShader(fullVS.Get(),nullptr,0); ctx->PSSetShader(compositePS.Get(),nullptr,0); ctx->PSSetConstantBuffers(1,1,mixCB.GetAddressOf());
		ID3D11ShaderResourceView* srvs[2]={a,b}; ctx->PSSetShaderResources(0,2,srvs);
		ctx->Draw(3,0);
		ID3D11ShaderResourceView* none[2]={}; ctx->PSSetShaderResources(0,2,none); ctx->OMSetRenderTargets(0,nullptr,nullptr);
		stats.composites++;
	}

	// A layer as a texture for the cross-fade (scenes are drawn into their own target, again only if their camera moved).
	ID3D11ShaderResourceView* LayerTexture(const Layer& l){
		if(l.still)return stillSrv.Get();
		if(!l.scene)return nullptr;
		Scene& s=*l.scene;
		if(FAILED(MakeTarget(s.rt)))return nullptr;
		if(!s.rtValid||s.rtCamX!=s.camX||s.rtCamY!=s.camY){ DrawScene(s,s.rt.rtv.Get()); s.rtValid=true; s.rtCamX=s.camX; s.rtCamY=s.camY; }
		return s.rt.srv.Get();
	}

	void DrawLayer(const Layer& l,ID3D11RenderTargetView* rtv){
		if(l.scene)DrawScene(*l.scene,rtv);
		else if(l.still&&stillSrv)Composite(stillSrv.Get(),stillSrv.Get(),0,rtv);
		else { float c[4]={bgNow[0],bgNow[1],bgNow[2],1}; ctx->ClearRenderTargetView(rtv,c); }
	}

	void DrawFrame(ID3D11RenderTargetView* rtv,float mix){
		if(fading){
			ID3D11ShaderResourceView* a=LayerTexture(from); ID3D11ShaderResourceView* b=LayerTexture(to);
			if(a&&b){ Composite(a,b,mix,rtv); return; }
			DrawLayer(mix<.5f?from:to,rtv); return;
		}
		DrawLayer(current,rtv);
	}

	double FadeMix(double now){ double t=fadeMs<=0?1:std::min(1.0,(now-fadeStart)/fadeMs); return fadeKind==1?EaseInOut(t):Smoothstep(t); }

	void StartFade(const Layer& target,int kind,double ms,double now){
		if(fading)EndFade();
		if(current.Empty()||ms<=0){ current=target; needDraw=true; return; }
		from=current; to=target; fading=true; fadeStart=now; fadeMs=ms; fadeKind=kind; needDraw=true;
	}
	void EndFade(){
		if(!fading)return; fading=false; current=to;
		if(from.scene){ from.scene->rt=Target(); from.scene->rtValid=false; }
		if(to.scene){ to.scene->rt=Target(); to.scene->rtValid=false; }
		bool wasStillIn=from.still&&!to.still;
		from=Layer(); to=Layer(); needDraw=true;
		if(wasStillIn&&!current.still)stillSrv.Reset();   // the fade-in still is not needed again
		// D3D11 destroys released resources only when the context flushes; the renderer may go idle right after this
		ctx->ClearState(); ctx->Flush();
	}

	Scene* LiveScene(){ if(fading)return to.scene.get(); return current.scene.get(); }

	// viewer.js updateCamera: eases toward pointer*sensitivity; dt in ms
	void UpdateCamera(Scene& s,float px,float py,double dt){
		float tx=px*sensitivity, ty=py*sensitivity;
		double n=std::min(10.0,std::max(1.0,(double)snappiness)), sp=std::pow(10.0,(n-10)*2/9), r=1-std::pow(1-sp,dt/(1000.0/60));
		s.camX=(float)(s.camX+(tx-s.camX)*r); s.camY=(float)(s.camY+(ty-s.camY)*r);
		// Stop once the nearest layer is within a fraction of a pixel of where the camera is going (viewer.js would keep
		// redrawing for a while at sub-pixel steps); snapping the rest is not visible.
		D3D11_VIEWPORT vp; D3D11_RECT sc; float aspect; SceneViewport(s,vp,sc,aspect);
		double pxPerUnit=s.h.focal*(vp.Width/s.h.originalWidth)/std::max(1e-3f,s.minZ);
		double eps=0.2/pxPerUnit;
		if(std::fabs(tx-s.camX)<eps&&std::fabs(ty-s.camY)<eps){ s.camX=tx; s.camY=ty; }
	}

	RECT primary={0,0,1,1}; double primaryAt=-1e9;
	void Pointer(float& px,float& py){
		if(pointerFixed){ px=fixedX; py=fixedY; return; }
		if(pointerCircle){ double t=Now()/1000*3.14159265; px=(float)(.5*std::cos(t)); py=(float)(.5*std::sin(t)); return; }
		int m=motion.load();
		// drift: a circle in pointer units, which span the screen on each axis, so it is an ellipse of the screen's shape
		if(m==2){ double a=Now()/DriftPeriodMs*2*3.14159265358979; px=(float)(DriftRadius*std::cos(a)); py=(float)(DriftRadius*std::sin(a)); return; }
		if(m==3){
			// handheld: the camera as if held in a hand - a slow, never-repeating wander (a few slow waves at unrelated
			// rates, so no loop is ever seen) with a faint quicker tremor on top, kept small; it moves all the time, gently
			double t=Now()/1000;
			auto wave=[&](double a1,double f1,double p1,double a2,double f2,double p2,double a3,double f3,double p3){
				return a1*std::sin(6.2831853*f1*t+p1)+a2*std::sin(6.2831853*f2*t+p2)+a3*std::sin(6.2831853*f3*t+p3); };
			double wx=wave(.20,.043,0.0,.11,.097,1.7,.05,.211,4.1), wy=wave(.15,.051,2.3,.09,.113,.6,.04,.237,3.3);
			double tx=.012*std::sin(6.2831853*.93*t+.4)+.007*std::sin(6.2831853*1.71*t+2.2), ty=.011*std::sin(6.2831853*1.07*t+1.3)+.006*std::sin(6.2831853*1.89*t+.9);
			px=(float)(wx+tx); py=(float)(wy+ty); return;
		}
		double t=Now(); if(t-primaryAt>1000){ primaryAt=t; MONITORINFO mi={sizeof(mi)}; POINT o={0,0}; if(GetMonitorInfoW(MonitorFromPoint(o,MONITOR_DEFAULTTOPRIMARY),&mi))primary=mi.rcMonitor; }
		POINT p; if(!GetCursorPos(&p)){ px=py=0; return; }
		double w=primary.right-primary.left, h=primary.bottom-primary.top;
		// same pointer as SpatialDesktopHost.UpdatePointer sends the web viewer. Both axes are mirrored the same way
		// (cursor below centre is positive y, cursor right of centre is negative x), so the view leans toward the cursor
		// on both axes; x used to be unmirrored and moved the opposite way.
		px=(float)-std::max(-1.0,std::min(1.0,(p.x-(primary.left+w/2))/(w*.5)));
		py=(float)std::max(-1.0,std::min(1.0,(p.y-(primary.top+h/2))/(h*.5)));
		if(m==1){ px=-px; py=-py; }   // the opposite way on both axes
	}

	bool Present(){
		ComPtr<ID3D11Texture2D> back; if(FAILED(swap->GetBuffer(0,IID_PPV_ARGS(&back))))return false;
		ComPtr<ID3D11RenderTargetView> rtv; if(FAILED(dev->CreateRenderTargetView(back.Get(),nullptr,&rtv)))return false;
		DrawFrame(rtv.Get(),fading?(float)FadeMix(Now()):0);
		HRESULT hr=swap->Present((motion.load()==2||motion.load()==3)&&!fading?presentInterval:1,0); stats.presents++;
		if(swapKind==2&&stats.presents==1)dcomp->Commit();
		if(hr==DXGI_STATUS_OCCLUDED){ occluded=true; occludedTest=Now(); stats.occlusions++; }
		return SUCCEEDED(hr);
	}

	void Capture(){
		Target t; HRESULT hr=MakeTarget(t);
		if(SUCCEEDED(hr)){
			bool savedFading=fading; float mix=fading?(float)FadeMix(Now()):0;
			if(captureMix>=0&&fading)mix=captureMix;
			DrawFrame(t.rtv.Get(),mix); fading=savedFading;
			D3D11_TEXTURE2D_DESC td; t.tex->GetDesc(&td); td.Usage=D3D11_USAGE_STAGING; td.BindFlags=0; td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
			ComPtr<ID3D11Texture2D> st; hr=dev->CreateTexture2D(&td,nullptr,&st);
			if(SUCCEEDED(hr)){
				ctx->CopyResource(st.Get(),t.tex.Get()); D3D11_MAPPED_SUBRESOURCE m;
				hr=ctx->Map(st.Get(),0,D3D11_MAP_READ,0,&m);
				if(SUCCEEDED(hr)){
					ComPtr<IWICImagingFactory> wic; hr=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&wic));
					ComPtr<IWICStream> fs; ComPtr<IWICBitmapEncoder> enc; ComPtr<IWICBitmapFrameEncode> fr;
					if(SUCCEEDED(hr))hr=wic->CreateStream(&fs);
					if(SUCCEEDED(hr))hr=fs->InitializeFromFilename(capturePath.c_str(),GENERIC_WRITE);
					if(SUCCEEDED(hr))hr=wic->CreateEncoder(GUID_ContainerFormatPng,nullptr,&enc);
					if(SUCCEEDED(hr))hr=enc->Initialize(fs.Get(),WICBitmapEncoderNoCache);
					if(SUCCEEDED(hr))hr=enc->CreateNewFrame(&fr,nullptr);
					if(SUCCEEDED(hr))hr=fr->Initialize(nullptr);
					if(SUCCEEDED(hr))hr=fr->SetSize(width,height);
					WICPixelFormatGUID pf=GUID_WICPixelFormat32bppBGRA; if(SUCCEEDED(hr))hr=fr->SetPixelFormat(&pf);
					if(SUCCEEDED(hr)&&pf!=GUID_WICPixelFormat32bppBGRA)hr=E_FAIL;
					std::vector<uint8_t> px((size_t)width*height*4);
					for(int y=0;y<height;y++){ uint8_t* d=px.data()+(size_t)y*width*4; memcpy(d,(uint8_t*)m.pData+(size_t)y*m.RowPitch,(size_t)width*4); for(int x=0;x<width;x++)d[x*4+3]=255; }
					if(SUCCEEDED(hr))hr=fr->WritePixels(height,width*4,(UINT)px.size(),px.data());
					if(SUCCEEDED(hr))hr=fr->Commit();
					if(SUCCEEDED(hr))hr=enc->Commit();
					ctx->Unmap(st.Get(),0);
				}
			}
		}
		captureResult=hr;
	}

	// Tests: GPU milliseconds per scene draw and per composite (offscreen, 100 each), in stats.benchScene/benchComposite.
	void Bench(){
		Scene* s=LiveScene(); Target a,b,c; if(!s||FAILED(MakeTarget(a))||FAILED(MakeTarget(b))||FAILED(MakeTarget(c))){ captureResult=E_FAIL; return; }
		D3D11_QUERY_DESC qd={D3D11_QUERY_EVENT,0}; ComPtr<ID3D11Query> q; dev->CreateQuery(&qd,&q);
		auto finish=[&]{ ctx->End(q.Get()); BOOL done=FALSE; while(ctx->GetData(q.Get(),&done,sizeof done,0)!=S_OK||!done)YieldProcessor(); };
		DrawScene(*s,a.rtv.Get()); finish();
		double t0=Now(); for(int i=0;i<100;i++){ s->camX+=1e-6f; DrawScene(*s,a.rtv.Get()); } finish(); stats.benchScene=(Now()-t0)/100;
		t0=Now(); for(int i=0;i<100;i++)Composite(a.srv.Get(),b.srv.Get(),.5f,c.rtv.Get()); finish(); stats.benchComposite=(Now()-t0)/100;
		captureResult=S_OK;
	}

	bool Busy(){
		if(fading||needDraw)return true;
		if((motion.load()==2||motion.load()==3)&&!paused.load()&&LiveScene())return true;   // drifting or handheld: the target moves every frame, without any input
		Scene* s=LiveScene(); if(!s)return false;
		return s->drawnX!=s->camX||s->drawnY!=s->camY||!CameraAtTarget(*s);
	}
	float lastPx=0, lastPy=0;
	bool CameraAtTarget(Scene& s){ return s.camX==lastPx*sensitivity&&s.camY==lastPy*sensitivity; }

	static LRESULT CALLBACK InputProc(HWND h,UINT m,WPARAM w,LPARAM l){ return DefWindowProcW(h,m,w,l); }

	void RenderRun(){
		CoInitializeEx(nullptr,COINIT_MULTITHREADED);
		// Raw mouse input (also in the background) only wakes this thread: while the mouse is still it sleeps.
		WNDCLASSW wc={}; wc.lpfnWndProc=InputProc; wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=L"SpatialNativeInput"; RegisterClassW(&wc);
		HWND input=CreateWindowExW(0,wc.lpszClassName,L"",0,0,0,0,0,HWND_MESSAGE,nullptr,wc.hInstance,nullptr);
		RAWINPUTDEVICE rid={1,2,RIDEV_INPUTSINK,input}; if(input)RegisterRawInputDevices(&rid,1,sizeof(rid));
		double frameMs=1000.0/60; { ComPtr<IDXGIOutput> out; if(SUCCEEDED(swap->GetContainingOutput(&out))){ DXGI_OUTPUT_DESC od; out->GetDesc(&od); DEVMODEW dm={}; dm.dmSize=sizeof(dm); if(EnumDisplaySettingsW(od.DeviceName,ENUM_CURRENT_SETTINGS,&dm)&&dm.dmDisplayFrequency>1)frameMs=1000.0/dm.dmDisplayFrequency; } }
		presentInterval=std::max(1,std::min(4,(int)std::lround(1000.0/frameMs/60)));   // drift: about 60 fps at any refresh rate
		double last=Now(); bool idle=true; bool first=true;
		// Paused: the camera no longer follows the pointer and raw input is switched off (no wake-ups from the mouse), so
		// nothing is drawn while the pointer moves. Everything else still draws as usual: slide cross-fades, colour
		// changes, fade in and fade out. So the desktop always shows what Matte thinks it shows (panel, tray), even if a
		// pause turns out to be wrong; these are a couple of seconds per slide.
		bool wasPaused=false;
		auto syncPause=[&]()->bool{
			bool p=paused.load(); if(p==wasPaused)return p;
			wasPaused=p;
			if(p){ if(input){ RAWINPUTDEVICE off={1,2,RIDEV_REMOVE,nullptr}; RegisterRawInputDevices(&off,1,sizeof(off)); } }
			else { needDraw=true; if(input)RegisterRawInputDevices(&rid,1,sizeof(rid)); }
			std::lock_guard<std::mutex> g(lock); stats.paused=p?1:0; if(p)stats.pauses++;
			return p;
		};
		while(!quit){
			bool isPaused=syncPause();
			bool busy=Busy();
			if(isPaused&&!first&&!fading&&!needDraw&&!occluded)WaitForSingleObject(wake,INFINITE);
			// Hidden behind windows (Present said so): nothing is drawn; checks every 100 ms whether it shows again.
			else if(occluded){ MsgWaitForMultipleObjects(1,&wake,FALSE,100,QS_RAWINPUT); idle=true; }
			else if(!busy){ MsgWaitForMultipleObjects(1,&wake,FALSE,100,QS_RAWINPUT); idle=true; }
			else WaitForSingleObjectEx(waitable,100,TRUE);
			MSG msg; while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){ TranslateMessage(&msg); DispatchMessageW(&msg); }
			if(quit)break;
			isPaused=syncPause();
			double now=Now(); double dt=idle?frameMs:std::min(now-last,100.0); last=now;
			// commands
			std::wstring capture; float cmix=-1;
			{
				std::lock_guard<std::mutex> g(lock);
				if(stillNew){
					stillNew=false; ComPtr<ID3D11ShaderResourceView> srv;
					if(!stillPixels.empty()&&stillW==width&&stillH==height&&SUCCEEDED(MakeTexture(dev.Get(),DXGI_FORMAT_B8G8R8A8_UNORM,stillW,stillH,stillPixels.data(),stillW*4,srv))){
						if(fading)EndFade(); stillSrv=srv; }
					stillPixels.clear(); stillPixels.shrink_to_fit();
					if(fadeOutMs<0&&current.Empty()){ current.still=!!stillSrv; needDraw=true; }
				}
				if(colorChanged){ colorChanged=false; memcpy(bgNow,pendingBg,sizeof bgNow);
					for(Layer* l:{&current,&from,&to})if(l->scene){ memcpy(l->scene->bg,pendingBg,sizeof pendingBg); l->scene->rtValid=false; }
					needDraw=true; }
				if(ready&&fadeOutMs<0){
					auto s=ready; ready.reset();
					if(pointerFixed||settle){ float px,py; Pointer(px,py); s->camX=px*sensitivity; s->camY=py*sensitivity; }
					else { float px,py; Pointer(px,py); for(int i=0;i<40;i++)UpdateCamera(*s,px,py,100); }   // settled before it fades in
					Layer l; l.scene=s;
					bool fromStill=current.still&&!fading;
					// from the still: quick when it is the same photo (Spatial switched on), a slide's cross-fade when it is the
					// photo before (the first 3D photo after flat ones; SnSetStillFade)
					int inMs=stillFadeMs.load();
					StartFade(l,fromStill&&inMs<=700?0:1,fromStill?inMs:1600,now);
					Emit(EvFirstScene,s->sequence);
				}
				if(fadeOutMs>=0&&!fadeOutStarted){
					fadeOutStarted=true;
					if(stillSrv&&(fading||current.scene)){ Layer l; l.still=true; StartFade(l,2,fadeOutMs,now); }
					else { if(stillSrv){ if(fading)EndFade(); current=Layer(); current.still=true; } needDraw=true; fadeOutDonePending=true; }
				}
				if(settle){ settle=false; Scene* s=LiveScene(); float px,py; Pointer(px,py); if(s){ s->camX=px*sensitivity; s->camY=py*sensitivity; } needDraw=true; }
				capture.swap(capturePath); cmix=captureMix;
			}
			float px,py; Pointer(px,py); lastPx=px; lastPy=py;
			if(Scene* s=LiveScene())if(!isPaused)UpdateCamera(*s,px,py,dt);
			if(!capture.empty()){ capturePath=capture; captureMix=cmix; if(capture==L"*bench")Bench(); else Capture(); capturePath.clear(); SetEvent(captureDone); }
			if(occluded&&now-occludedTest>=100){ occludedTest=now; if(swap->Present(0,DXGI_PRESENT_TEST)==S_OK){ occluded=false; needDraw=true; } }
			Scene* live=LiveScene();
			bool moved=live&&(live->drawnX!=live->camX||live->drawnY!=live->camY);
			bool presented=false;
			if((fading||needDraw||moved||first)&&!occluded){
				if(idle)WaitForSingleObjectEx(waitable,100,TRUE);
				needDraw=false; presented=Present(); idle=false;
				if(first){ first=false; SetEvent(firstPresent); }
			}
			else if(!occluded)idle=true;
			if(fadeOutDonePending&&(presented||occluded)){ fadeOutDonePending=false; Emit(EvFadeOutDone,0); }
			if(fading&&Now()-fadeStart>=fadeMs){
				int kind=fadeKind; int seq=to.scene?to.scene->sequence:0; EndFade();
				if(occluded)needDraw=true; else { Present(); needDraw=false; }
				Emit(kind==2?EvFadeOutDone:EvTransitionDone,seq);
			}
		}
		if(input){ RAWINPUTDEVICE off={1,2,RIDEV_REMOVE,nullptr}; RegisterRawInputDevices(&off,1,sizeof(off)); DestroyWindow(input); }
		CoUninitialize();
	}

	void LoaderRun(){
		ComApartment apartment; testFault=loaderFault; allocationBytes=0;
		for(;;){
			int seq=0; Failure failure={};
			// Includes request transfer, make_shared, all parsing/decoding/uploading, and ready publication.
			try{ // LOADER_BOUNDARY_BEGIN
				std::wstring path;
				{ std::unique_lock<std::mutex> g(loadLock); loadCv.wait(g,[&]{ return loadPending||quit.load(); }); if(quit)break; seq=loadSequence; path.swap(loadPath); loadPending=false; }
				allocationBytes=0; double t0=Now(); if(testFault==1)throw std::bad_alloc(); auto s=std::make_shared<Scene>(); s->sequence=seq;
				Runtime(SUCCEEDED(apartment.hr),"loader COM initialization failed",ReasonWic); Load(path,*s);
				std::lock_guard<std::mutex> g(lock);
				if(seq==latestSequence&&!quit&&!testOnly){ memcpy(s->bg,pendingBg,sizeof pendingBg); s->backdrop=pendingBackdrop; ready=s; stats.loads++; stats.lastLoadMs=Now()-t0; stats.minZ=s->minZ; SetEvent(wake); }
			}catch(const Failure& f){ failure=f; }catch(const std::bad_alloc&){ failure=AllocationFailure(); }catch(...){ failure=UnexpectedFailure(); } // LOADER_BOUNDARY_END
			if(failure.kind)Report(seq,failure);
			if(testOnly)break;
		}
		testFault=0;
	}

	void Load(const std::wstring& path,Scene& s){
		std::vector<uint8_t> file; std::vector<Vertex> verts; std::vector<uint32_t> idx; size_t co,ao;
		PrepareSpatial(path.c_str(),file,s.h,verts,idx,s.minZ,s.maxZ,co,ao); auto wic=MakeWic();
		Runtime(testFault!=6,"vertex/index buffer creation failed",ReasonBuffer); Runtime(testFault!=7,"texture creation failed",ReasonTexture);
		if(testOnly){ DecodeJpegBounded(wic.Get(),file.data()+co,s.h.colorBytes,s.h.atlasWidth,s.h.atlasHeight); DecodeJpegBounded(wic.Get(),file.data()+ao,s.h.alphaBytes,s.h.atlasWidth,s.h.atlasHeight); return; }
		D3D11_BUFFER_DESC bd={}; bd.Usage=D3D11_USAGE_IMMUTABLE; bd.BindFlags=D3D11_BIND_VERTEX_BUFFER; bd.ByteWidth=(UINT)(verts.size()*sizeof(Vertex));
		D3D11_SUBRESOURCE_DATA sd={verts.data()}; Runtime(SUCCEEDED(dev->CreateBuffer(&bd,&sd,&s.vb)),"vertex buffer creation failed",ReasonBuffer);
		bd.BindFlags=D3D11_BIND_INDEX_BUFFER; bd.ByteWidth=(UINT)(idx.size()*4); sd.pSysMem=idx.data(); Runtime(SUCCEEDED(dev->CreateBuffer(&bd,&sd,&s.ib)),"index buffer creation failed",ReasonBuffer);
		s.indexCount=(UINT)idx.size();
		DecodeJpegToTexture(wic.Get(),dev.Get(),ctx.Get(),file.data()+co,s.h.colorBytes,s.h.atlasWidth,s.h.atlasHeight,s.color);
		std::vector<uint8_t> px; DecodeJpeg(wic.Get(),file.data()+ao,s.h.alphaBytes,px,s.h.atlasWidth,s.h.atlasHeight);
		Runtime(SUCCEEDED(MakeTexture(dev.Get(),DXGI_FORMAT_R8_UNORM,s.h.atlasWidth,s.h.atlasHeight,px.data(),s.h.atlasWidth,s.alpha)),"alpha texture creation failed",ReasonTexture);
	}
};

static void SetRgb(float* out,unsigned rgb){ out[0]=((rgb>>16)&255)/255.f; out[1]=((rgb>>8)&255)/255.f; out[2]=(rgb&255)/255.f; }
template<class F> static Failure Boundary(F&& action)noexcept{
	try{ action(); return {}; }catch(const Failure& f){ return f; }catch(const std::bad_alloc&){ return AllocationFailure(); }catch(...){ return UnexpectedFailure(); }
}
static int WriteFailure(Failure f,wchar_t* error,int len){ if(error&&len>0){ if(f.kind)_snwprintf_s(error,len,_TRUNCATE,L"%d: %hs",f.reason,f.text); else error[0]=0; } return f.kind; }
static void ReportApiFailure(Renderer* r,Failure f,int seq=-1){ if(!f.kind)return; if(seq<0){ std::lock_guard<std::mutex> g(r->lock); seq=r->latestSequence; } r->Report(seq,f); }
static void ValidateFile(const wchar_t* path,bool decode){
	allocationBytes=0; std::vector<uint8_t> file; Header h={}; std::vector<Vertex> verts; std::vector<uint32_t> idx; float minZ,maxZ; size_t co,ao;
	PrepareSpatial(path,file,h,verts,idx,minZ,maxZ,co,ao);
	if(decode){ ComApartment apartment; Runtime(SUCCEEDED(apartment.hr)||apartment.hr==RPC_E_CHANGED_MODE,"validation COM initialization failed",ReasonWic); auto wic=MakeWic();
		DecodeJpegBounded(wic.Get(),file.data()+co,h.colorBytes,h.atlasWidth,h.atlasHeight); DecodeJpegBounded(wic.Get(),file.data()+ao,h.alphaBytes,h.atlasWidth,h.atlasHeight);
	}
}

extern "C" {
__declspec(dllexport) int __stdcall SnLimits(uint64_t* values,int count){
	const uint64_t limits[]={MaxFileBytes,MaxDenseSlots,MaxBlocks,MaxVertices,MaxIndices,MaxMeshBytes,MaxAtlasPixels,MaxValidationBytes,MaxSlices,MaxBlockSize,MaxDimension,HeaderBytes};
	const int n=(int)(sizeof(limits)/sizeof(limits[0])); if(values&&count>=n)memcpy(values,limits,sizeof limits); return n;
}
// 0 = OK, 3 = content failure, 5 = runtime failure. A diagnostic starts with the stable numeric reason code.
__declspec(dllexport) int __stdcall SnValidate(const wchar_t* path,wchar_t* error,int len){ return WriteFailure(Boundary([&]{ ValidateFile(path,true); }),error,len); }
__declspec(dllexport) int __stdcall SnValidateStructure(const wchar_t* path,wchar_t* error,int len){ return WriteFailure(Boundary([&]{ ValidateFile(path,false); }),error,len); }
// Query in the callback before dispatching to the UI. The bounded history also tolerates delayed readers;
// an evicted/unknown sequence returns 0, never another sequence's reason.
__declspec(dllexport) int __stdcall SnLastError(void* p,int sequence,int* reason,wchar_t* error,int len){
	if(!p){ if(reason)*reason=0; if(error&&len>0)error[0]=0; return 0; } // Dispose may clear the managed handle before joining callbacks.
	Failure f={}; auto r=(Renderer*)p;
	Failure query=Boundary([&]{ std::lock_guard<std::mutex> g(r->errorLock); for(unsigned n=0;n<std::min(r->errorNext,64u);n++){ auto& e=r->loadErrors[(r->errorNext-1-n)%64]; if(e.sequence==sequence){ f=e.failure; break; } } });
	if(query.kind)f=query; if(reason)*reason=f.reason; return WriteFailure(f,error,len);
}
// Test-only operation, explicitly invoked: run the actual LoaderRun on a real worker without a window/D3D.
// Faults never depend on environment variables or file contents. They are scoped to this one renderer/thread.
__declspec(dllexport) int __stdcall SnTestLoader(const wchar_t* path,int fault,SnCallback callback,int* reason,wchar_t* error,int len){
	int kind=0; Failure f=Boundary([&]{ Renderer r; r.testOnly=true; r.loaderFault=fault; r.callback=callback; r.loadPath=path; r.loadPending=true; r.loadSequence=r.latestSequence=1;
		std::thread worker([&]{ r.LoaderRun(); }); worker.join(); kind=SnLastError(&r,1,reason,error,len);
	}); if(f.kind){ if(reason)*reason=f.reason; return WriteFailure(f,error,len); } return kind;
}
__declspec(dllexport) void* __stdcall SnCreate(HWND hwnd,int width,int height,unsigned rgb,SnCallback cb,wchar_t* error,int errorLen){
	Renderer* result=nullptr; Failure failure=Boundary([&]{
	auto r=std::make_unique<Renderer>(); r->hwnd=hwnd; r->width=width; r->height=height; r->callback=cb; SetRgb(r->pendingBg,rgb); memcpy(r->bgNow,r->pendingBg,sizeof r->bgNow);
	std::string err; HRESULT hr=r->Init(err);
	if(FAILED(hr)){ if(error&&errorLen>0)_snwprintf_s(error,errorLen,_TRUNCATE,L"%hs (0x%08X)",err.c_str(),(unsigned)hr); return; }
	r->stats.swapKind=r->swapKind; r->stats.width=width; r->stats.height=height;
	FileHandle wake(CreateEventW(nullptr,FALSE,FALSE,nullptr)),first(CreateEventW(nullptr,TRUE,FALSE,nullptr)),capture(CreateEventW(nullptr,FALSE,FALSE,nullptr));
	Runtime(wake.value&&first.value&&capture.value,"renderer event creation failed",ReasonUnexpected);
	r->wake=wake.Release(); r->firstPresent=first.Release(); r->captureDone=capture.Release(); result=r.release();
	}); if(failure.kind)WriteFailure(failure,error,errorLen); return result;
}
// The flat still shown until the first scene fades in (BGRA, the window's size). Call before SnStart.
__declspec(dllexport) void __stdcall SnSetStill(void* p,const void* bgra,int w,int h,int stride){
	auto r=(Renderer*)p; Failure failure=Boundary([&]{ std::lock_guard<std::mutex> g(r->lock);
	r->stillPixels.resize((size_t)w*h*4); for(int y=0;y<h;y++)memcpy(r->stillPixels.data()+(size_t)y*w*4,(const uint8_t*)bgra+(size_t)y*stride,(size_t)w*4);
	r->stillW=w; r->stillH=h; r->stillNew=true; SetEvent(r->wake);
	}); ReportApiFailure(r,failure);
}
// How long the first scene takes to come in over the still (700 ms by default; a slide's 1600 when the still is the
// photo before). Before SnStart.
__declspec(dllexport) void __stdcall SnSetStillFade(void* p,int ms){ ((Renderer*)p)->stillFadeMs=ms<0?700:ms; }
__declspec(dllexport) int __stdcall SnStart(void* p,int waitMs){
	auto r=(Renderer*)p; Failure failure=Boundary([&]{
		r->renderThread=std::thread([r]{ ReportApiFailure(r,Boundary([&]{ r->RenderRun(); })); });
		r->loaderThread=std::thread([r]{ r->LoaderRun(); });
	});
	if(failure.kind){ ReportApiFailure(r,failure); r->quit=true; SetEvent(r->wake); r->loadCv.notify_all(); if(r->renderThread.joinable())r->renderThread.join(); if(r->loaderThread.joinable())r->loaderThread.join(); return 0; }
	return WaitForSingleObject(r->firstPresent,waitMs)==WAIT_OBJECT_0;
}
__declspec(dllexport) void __stdcall SnShow(void* p,const wchar_t* path,unsigned rgb,int sequence){
	auto r=(Renderer*)p;
	Failure failure=Boundary([&]{
	{ std::lock_guard<std::mutex> g(r->lock); r->latestSequence=sequence; SetRgb(r->pendingBg,rgb); }
	{ std::lock_guard<std::mutex> g(r->loadLock); r->loadPath=path; r->loadSequence=sequence; r->loadPending=true; }
	r->loadCv.notify_one();
	}); ReportApiFailure(r,failure,sequence);
}
// The ambient background for the scenes shown after this (BGRA, exactly the window's size); null = the colour again.
__declspec(dllexport) void __stdcall SnSetBackdrop(void* p,const void* bgra,int w,int h,int stride){
	auto r=(Renderer*)p; Failure failure=Boundary([&]{ ComPtr<ID3D11ShaderResourceView> srv;
	if(bgra&&w==r->width&&h==r->height){
		std::vector<uint8_t> px((size_t)w*h*4); for(int y=0;y<h;y++)memcpy(px.data()+(size_t)y*w*4,(const uint8_t*)bgra+(size_t)y*stride,(size_t)w*4);
		if(FAILED(MakeTexture(r->dev.Get(),DXGI_FORMAT_B8G8R8A8_UNORM,w,h,px.data(),w*4,srv)))srv.Reset();
	}
	std::lock_guard<std::mutex> g(r->lock); r->pendingBackdrop=srv;
	}); ReportApiFailure(r,failure);
}
// Live colour change: every scene on screen, and the one still loading.
__declspec(dllexport) void __stdcall SnSetColor(void* p,unsigned rgb){ auto r=(Renderer*)p; std::lock_guard<std::mutex> g(r->lock); SetRgb(r->pendingBg,rgb); r->colorChanged=true; SetEvent(r->wake); }
// Fades to the given still over ms, then EvFadeOutDone (the host closes). Scenes arriving later are ignored.
__declspec(dllexport) void __stdcall SnFadeOut(void* p,const void* bgra,int w,int h,int stride,int ms){
	auto r=(Renderer*)p; Failure failure=Boundary([&]{ std::lock_guard<std::mutex> g(r->lock);
	r->stillPixels.resize((size_t)w*h*4); for(int y=0;y<h;y++)memcpy(r->stillPixels.data()+(size_t)y*w*4,(const uint8_t*)bgra+(size_t)y*stride,(size_t)w*4);
	r->stillW=w; r->stillH=h; r->stillNew=true; r->fadeOutMs=ms; SetEvent(r->wake);
	}); ReportApiFailure(r,failure);
}
// Tests: mode 1 = fixed pointer (x,y in -1..1), 0 = cursor; settle = put the camera on its target now.
__declspec(dllexport) void __stdcall SnSetPointer(void* p,int mode,float x,float y,int settle){ auto r=(Renderer*)p; std::lock_guard<std::mutex> g(r->lock); r->pointerFixed=mode==1; r->pointerCircle=mode==2; r->fixedX=x; r->fixedY=y; if(settle)r->settle=true; SetEvent(r->wake); }
__declspec(dllexport) int __stdcall SnCapture(void* p,const wchar_t* png,float mix){
	auto r=(Renderer*)p; { std::lock_guard<std::mutex> g(r->lock); r->capturePath=png; r->captureMix=mix; } SetEvent(r->wake);
	if(WaitForSingleObject(r->captureDone,10000)!=WAIT_OBJECT_0)return -1; return (int)r->captureResult;
}
// 1: stop presenting until 0 (the desktop is not visible). Commands are still taken; see Renderer::paused.
__declspec(dllexport) void __stdcall SnSetMotion(void* p,int mode){ auto r=(Renderer*)p; r->motion=mode<0||mode>3?0:mode; SetEvent(r->wake); }
__declspec(dllexport) void __stdcall SnSetPaused(void* p,int on){ auto r=(Renderer*)p; r->paused=on!=0; SetEvent(r->wake); }
__declspec(dllexport) void __stdcall SnStats(void* p,SnStatsData* out){ auto r=(Renderer*)p; std::lock_guard<std::mutex> g(r->lock); *out=r->stats; }
__declspec(dllexport) void __stdcall SnDestroy(void* p){
	auto r=(Renderer*)p; if(!r)return;
	r->quit=true; SetEvent(r->wake); { std::lock_guard<std::mutex> g(r->loadLock); } r->loadCv.notify_all();
	if(r->renderThread.joinable())r->renderThread.join();
	if(r->loaderThread.joinable())r->loaderThread.join();
	r->callback=nullptr;
	if(r->ctx){ r->ctx->ClearState(); r->ctx->Flush(); }
	if(r->waitable)CloseHandle(r->waitable); CloseHandle(r->wake); CloseHandle(r->firstPresent); CloseHandle(r->captureDone);
	delete r;
}
}
