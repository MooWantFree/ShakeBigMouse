// Interactive Windows 11 diagnostic. Build with MSVC /std:c++20 /EHsc.
// Read back a small patch around the pointer; output numerical statistics only.
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <algorithm>
#include <cstdio>
#include <vector>
#pragma comment(lib,"windowsapp.lib")
#pragma comment(lib,"d3d11.lib")
#pragma comment(lib,"user32.lib")
#pragma comment(lib,"gdi32.lib")
using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
constexpr int side=144, pad=12;
struct RestorePoint { POINT p; ~RestorePoint(){SetPhysicalCursorPos(p.x,p.y);} };
static int width(HCURSOR c) {
    ICONINFO ii{}; if(!GetIconInfo(c,&ii))return 0;
    BITMAP bm{}; GetObject(ii.hbmColor?ii.hbmColor:ii.hbmMask,sizeof(bm),&bm);
    DeleteObject(ii.hbmColor); DeleteObject(ii.hbmMask); return bm.bmWidth;
}
static void move(int x,int y) {
    int l=GetSystemMetrics(SM_XVIRTUALSCREEN), t=GetSystemMetrics(SM_YVIRTUALSCREEN);
    INPUT i{}; i.type=INPUT_MOUSE;
    i.mi.dx=LONG((x-l+.5)*65536/GetSystemMetrics(SM_CXVIRTUALSCREEN));
    i.mi.dy=LONG((y-t+.5)*65536/GetSystemMetrics(SM_CYVIRTUALSCREEN));
    i.mi.dwFlags=MOUSEEVENTF_MOVE|MOUSEEVENTF_ABSOLUTE|MOUSEEVENTF_VIRTUALDESK;
    if(SendInput(1,&i,sizeof(i))!=1)throw hresult_error(E_FAIL,L"Cannot inject motion");
}
int main() try {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    init_apartment(apartment_type::multi_threaded);
    RestorePoint saved{}; GetPhysicalCursorPos(&saved.p);
    HMONITOR monitor=MonitorFromPoint(saved.p,MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi{sizeof(mi)}; GetMonitorInfo(monitor,&mi);
    int x=(mi.rcWork.left+mi.rcWork.right)/2,y=(mi.rcWork.top+mi.rcWork.bottom)/2;
    move(x,y); Sleep(250);
    wchar_t cls[100]{}; GetClassNameW(WindowFromPoint({x,y}),cls,100);
    printf("Window class: %ls\n",cls);
    if(wcscmp(cls,L"SysListView32") && wcscmp(cls,L"WorkerW") && wcscmp(cls,L"Progman") && wcscmp(cls,L"SHELLDLL_DefView"))
        throw hresult_error(E_FAIL,L"Expected the Windows desktop at the probe point");
    com_ptr<ID3D11Device> dev; com_ptr<ID3D11DeviceContext> ctx;
    check_hresult(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,0,D3D11_SDK_VERSION,dev.put(),nullptr,ctx.put()));
    com_ptr<::IInspectable> inspect;
    check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dev.as<IDXGIDevice>().get(),inspect.put()));
    auto interop=get_activation_factory<GraphicsCaptureItem,IGraphicsCaptureItemInterop>();
    GraphicsCaptureItem item{nullptr}; check_hresult(interop->CreateForMonitor(monitor,guid_of<GraphicsCaptureItem>(),put_abi(item)));
    auto pool=Direct3D11CaptureFramePool::CreateFreeThreaded(inspect.as<IDirect3DDevice>(),
        DirectXPixelFormat::B8G8R8A8UIntNormalized,2,item.Size());
    auto session=pool.CreateCaptureSession(item); session.IsCursorCaptureEnabled(false);
    session.StartCapture();
    D3D11_TEXTURE2D_DESC desc{}; desc.Width=desc.Height=side; desc.MipLevels=desc.ArraySize=1;
    desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count=1;
    desc.Usage=D3D11_USAGE_STAGING; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    com_ptr<ID3D11Texture2D> staging; check_hresult(dev->CreateTexture2D(&desc,nullptr,staging.put()));
    UINT sx=x-mi.rcMonitor.left-pad,sy=y-mi.rcMonitor.top-pad;
    D3D11_BOX box{sx,sy,0,sx+side,sy+side,1};
    long long frameTicks=0;
    auto read=[&](std::vector<unsigned>& pixels){
        auto frame=pool.TryGetNextFrame(); if(!frame)return false;
        frameTicks=frame.SystemRelativeTime().count();
        auto access=frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        com_ptr<ID3D11Texture2D> tex; check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D),tex.put_void()));
        ctx->CopySubresourceRegion(staging.get(),0,0,0,0,tex.get(),0,&box);
        D3D11_MAPPED_SUBRESOURCE map{}; check_hresult(ctx->Map(staging.get(),0,D3D11_MAP_READ,0,&map));
        for(int n=0;n<side;n++)memcpy(pixels.data()+n*side,(BYTE*)map.pData+n*map.RowPitch,side*4);
        ctx->Unmap(staging.get(),0); return true;
    };
    std::vector<unsigned> bg(side*side),pixels(bg.size()),reference;
    ULONGLONG until=GetTickCount64()+3000; while(!read(bg)) {if(GetTickCount64()>until)throw hresult_error(E_FAIL,L"No baseline frame"); Sleep(3);}
    session.IsCursorCaptureEnabled(true);
    auto sample=[&](const char* phase,int ms,int offset) {
        ULONGLONG start=GetTickCount64(); int previous=-999,prevWidth=-1,prevLeft=-1,prevHeight=-1,prevDiff=-1; HCURSOR prevCursor=nullptr;
        long long firstTicks=0;
        std::vector<unsigned> last;
        while(GetTickCount64()-start<(unsigned)ms) {
            if(!read(pixels)){Sleep(1);continue;}
            int l=side,r=-1,t=side,b=-1,count=0;
            for(int j=0;j<side;j++)for(int i=0;i<side;i++){
                auto a=bg[j*side+i],z=pixels[j*side+i]; int delta=0;
                for(int k=0;k<24;k+=8)delta=std::max(delta,abs(int((a>>k)&255)-int((z>>k)&255)));
                if(delta>24){l=std::min(l,i);r=std::max(r,i);t=std::min(t,j);b=std::max(b,j);count++;}
            }
            if(!firstTicks)firstTicks=frameTicks;
            int diff=0,maxDiff=0,stepPixels=0,maxStep=0;
            if(!last.empty())for(size_t j=0;j<pixels.size();j++){
                int d=0;for(int k=0;k<24;k+=8)d=std::max(d,abs(int((last[j]>>k)&255)-int((pixels[j]>>k)&255)));
                stepPixels+=(d>8);maxStep=std::max(maxStep,d);
            }
            if(!strcmp(phase,"before"))reference=pixels;
            if(!reference.empty() && !offset)
                for(size_t j=0;j<pixels.size();j++) {
                    int d=0; for(int k=0;k<24;k+=8)d=std::max(d,abs(int((reference[j]>>k)&255)-int((pixels[j]>>k)&255)));
                    if(d>8)diff++;maxDiff=std::max(maxDiff,d);
                }
            CURSORINFO ci{sizeof(ci)};GetCursorInfo(&ci);
            int cw=width(ci.hCursor);
            if(count!=previous||cw!=prevWidth||l!=prevLeft||b-t!=prevHeight||ci.hCursor!=prevCursor||diff!=prevDiff||maxStep){
                printf("%s,%.1f,%d,%d,%d,%d,%d,%d,%d,%p,%lu,%d,%d,%d,%d\n",phase,(frameTicks-firstTicks)/10000.0,cw,
                    l-pad-offset,t-pad,r<l?0:r-l+1,b<t?0:b-t+1,count,width(LoadCursor(nullptr,IDC_ARROW)),ci.hCursor,ci.flags,offset?-1:diff,offset?-1:maxDiff,stepPixels,maxStep);
                prevDiff=diff;
                previous=count;prevWidth=cw;prevLeft=l;prevHeight=b-t;prevCursor=ci.hCursor;
            }
            last=pixels;
        }
    };
    printf("phase,ms,active_canvas,left,top,visible_w,visible_h,pixels,stock_canvas,active_handle,flags,changed_from_rest,max_channel_error,step_pixels,max_step\n");
    move(x+1,y); Sleep(40); move(x,y); sample("before",450,0);
    int a=std::min<LONG>(850,(mi.rcWork.right-mi.rcWork.left)/3);
    for(int i=0;i<=88;i++){int p=i%16;move(x+(p<=8?p:16-p)*a/4-a,y);Sleep(7);}
    move(x,y); sample("release",3800,0);
    move(x+1,y);sample("nudge",350,1);move(x,y);sample("returned",350,0);
    session.Close(); pool.Close(); return 0;
} catch(hresult_error const& e){fprintf(stderr,"Capture failed: %08x %ls\n",unsigned(e.code().value),e.message().c_str());return 1;}
