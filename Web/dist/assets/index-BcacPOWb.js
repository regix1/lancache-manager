const __vite__mapDeps=(i,m=__vite__mapDeps,d=(m.f||(m.f=["assets/Dashboard-3KyMb8ij.js","assets/react-vendor-d8C5-Seo.js","assets/vendor-BmYUbuLR.js","assets/tanstack-CPlEqbpz.js","assets/Card-CJZki2sU.js","assets/charts-B1PwCyAI.js","assets/Tooltip-CWBRvCaf.js","assets/DownloadsTab-9z7ll2Na.js","assets/Alert-DbfzQSrM.js","assets/ClientsTab-DcfTH012.js","assets/ServicesTab-DJIVowUo.js","assets/ManagementTab-Cga9Y9aa.js","assets/signalr-C7Jyn4vH.js"])))=>i.map(i=>d[i]);
var de=Object.defineProperty;var me=(m,t,r)=>t in m?de(m,t,{enumerable:!0,configurable:!0,writable:!0,value:r}):m[t]=r;var L=(m,t,r)=>me(m,typeof t!="symbol"?t+"":t,r);import{r as d,j as o,M as ue,W as V,L as fe,D as ge,U as be,S as pe,X as ve,a as ye,C as xe,b as we,c as Ce,R as Se}from"./react-vendor-d8C5-Seo.js";import{t as Te}from"./vendor-BmYUbuLR.js";import"./tanstack-CPlEqbpz.js";(function(){const t=document.createElement("link").relList;if(t&&t.supports&&t.supports("modulepreload"))return;for(const e of document.querySelectorAll('link[rel="modulepreload"]'))a(e);new MutationObserver(e=>{for(const s of e)if(s.type==="childList")for(const n of s.addedNodes)n.tagName==="LINK"&&n.rel==="modulepreload"&&a(n)}).observe(document,{childList:!0,subtree:!0});function r(e){const s={};return e.integrity&&(s.integrity=e.integrity),e.referrerPolicy&&(s.referrerPolicy=e.referrerPolicy),e.crossOrigin==="use-credentials"?s.credentials="include":e.crossOrigin==="anonymous"?s.credentials="omit":s.credentials="same-origin",s}function a(e){if(e.ep)return;e.ep=!0;const s=r(e);fetch(e.href,s)}})();const Be="modulepreload",$e=function(m){return"/"+m},re={},z=function(t,r,a){let e=Promise.resolve();if(r&&r.length>0){document.getElementsByTagName("link");const n=document.querySelector("meta[property=csp-nonce]"),c=(n==null?void 0:n.nonce)||(n==null?void 0:n.getAttribute("nonce"));e=Promise.allSettled(r.map(i=>{if(i=$e(i),i in re)return;re[i]=!0;const p=i.endsWith(".css"),x=p?'[rel="stylesheet"]':"";if(document.querySelector(`link[href="${i}"]${x}`))return;const l=document.createElement("link");if(l.rel=p?"stylesheet":Be,p||(l.as="script"),l.crossOrigin="",l.href=i,c&&l.setAttribute("nonce",c),document.head.appendChild(l),p)return new Promise((h,f)=>{l.addEventListener("load",h),l.addEventListener("error",()=>f(new Error(`Unable to preload CSS for ${i}`)))})}))}function s(n){const c=new Event("vite:preloadError",{cancelable:!0});if(c.payload=n,window.dispatchEvent(c),!c.defaultPrevented)throw n}return e.then(n=>{for(const c of n||[])c.status==="rejected"&&s(c.reason);return t().catch(s)})},u="/api",N=["steam","epic","origin","blizzard","wsus","riot"],Ae=5e3,qe=["B","KB","MB","GB","TB","PB"],Xe={DASHBOARD_CARD_ORDER:"lancache_dashboard_card_order",DASHBOARD_CARD_VISIBILITY:"lancache_dashboard_card_visibility"},J={},Ie=()=>{if(!(typeof import.meta<"u"&&(J!=null&&J.VITE_API_URL)))return""},Z=Ie();class De{constructor(){L(this,"deviceId");L(this,"isAuthenticated");L(this,"authChecked");this.deviceId=this.getOrCreateDeviceId(),this.isAuthenticated=!1,this.authChecked=!1}getOrCreateDeviceId(){let t=localStorage.getItem("lancache_device_id");return t||(t="xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g,function(r){const a=Math.random()*16|0;return(r==="x"?a:a&3|8).toString(16)}),localStorage.setItem("lancache_device_id",t)),t}async checkAuth(){try{const t=await fetch(`${Z}/api/auth/check`,{headers:{"X-Device-Id":this.deviceId}});if(t.ok){const r=await t.json();return this.isAuthenticated=r.isAuthenticated,this.authChecked=!0,r}return this.isAuthenticated=!1,this.authChecked=!0,{requiresAuth:!0,isAuthenticated:!1}}catch(t){return console.error("Auth check failed:",t),this.authChecked=!0,{requiresAuth:!1,isAuthenticated:!1,error:t.message}}}async register(t,r=null){try{const a=await fetch(`${Z}/api/auth/register`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({deviceId:this.deviceId,apiKey:t,deviceName:r||this.getDeviceName()})}),e=await a.json();return a.ok&&e.success?(this.isAuthenticated=!0,localStorage.setItem("lancache_auth_registered","true"),{success:!0,message:e.message}):{success:!1,message:e.message||"Registration failed"}}catch(a){return console.error("Registration failed:",a),{success:!1,message:a.message||"Network error during registration"}}}async regenerateApiKey(){try{const t=await fetch(`${Z}/api/auth/regenerate-key`,{method:"POST",headers:{"Content-Type":"application/json","X-Device-Id":this.deviceId}}),r=await t.json();return t.ok&&r.success?(this.clearAuth(),this.isAuthenticated=!1,{success:!0,message:r.message,warning:r.warning}):{success:!1,message:r.message||"Failed to regenerate API key"}}catch(t){return console.error("Failed to regenerate API key:",t),{success:!1,message:t.message||"Network error while regenerating API key"}}}getDeviceName(){const t=navigator.userAgent;let r="Unknown OS",a="Unknown Browser";return t.indexOf("Win")!==-1?r="Windows":t.indexOf("Mac")!==-1?r="macOS":t.indexOf("Linux")!==-1?r="Linux":t.indexOf("Android")!==-1?r="Android":t.indexOf("iOS")!==-1&&(r="iOS"),t.indexOf("Chrome")!==-1?a="Chrome":t.indexOf("Safari")!==-1?a="Safari":t.indexOf("Firefox")!==-1?a="Firefox":t.indexOf("Edge")!==-1&&(a="Edge"),`${a} on ${r}`}getAuthHeaders(){return{"X-Device-Id":this.deviceId}}handleUnauthorized(){this.isAuthenticated=!1,localStorage.removeItem("lancache_auth_registered")}clearAuth(){this.isAuthenticated=!1,localStorage.removeItem("lancache_auth_registered")}isRegistered(){return localStorage.getItem("lancache_auth_registered")==="true"}}const G=new De;class I{static async handleResponse(t){if(t.status===401){G.handleUnauthorized();const r=await t.text().catch(()=>"");throw new Error(`Authentication required: ${r||"Please provide API key"}`)}if(!t.ok){const r=await t.text().catch(()=>"");throw new Error(`HTTP ${t.status}: ${r||t.statusText}`)}return t.json()}static getHeaders(t={}){return{...G.getAuthHeaders(),...t}}static async getCacheInfo(t){try{const r=await fetch(`${u}/management/cache`,{signal:t,headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw r.name==="AbortError"||console.error("getCacheInfo error:",r),r}}static async getActiveDownloads(t){try{const r=await fetch(`${u}/downloads/active`,{signal:t,headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw r.name==="AbortError"||console.error("getActiveDownloads error:",r),r}}static async getLatestDownloads(t,r=50){try{const e=await fetch(`${u}/downloads/latest?count=${r==="unlimited"?9999:r}`,{signal:t,headers:this.getHeaders()});return await this.handleResponse(e)}catch(a){throw a.name==="AbortError"||console.error("getLatestDownloads error:",a),a}}static async getClientStats(t){try{const r=await fetch(`${u}/stats/clients`,{signal:t,headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw r.name==="AbortError"||console.error("getClientStats error:",r),r}}static async getServiceStats(t,r=null){try{const a=r?`${u}/stats/services?since=${r}`:`${u}/stats/services`,e=await fetch(a,{signal:t,headers:this.getHeaders()});return await this.handleResponse(e)}catch(a){throw a.name==="AbortError"||console.error("getServiceStats error:",a),a}}static async getDashboardStats(t="24h",r){try{const a=await fetch(`${u}/stats/dashboard?period=${t}`,{signal:r,headers:this.getHeaders()});return await this.handleResponse(a)}catch(a){throw a.name==="AbortError"||console.error("getDashboardStats error:",a),a}}static async getCacheEffectiveness(t="24h",r){try{const a=await fetch(`${u}/stats/cache-effectiveness?period=${t}`,{signal:r,headers:this.getHeaders()});return await this.handleResponse(a)}catch(a){throw a.name==="AbortError"||console.error("getCacheEffectiveness error:",a),a}}static async getTimelineStats(t="24h",r="hourly",a){try{const e=await fetch(`${u}/stats/timeline?period=${t}&interval=${r}`,{signal:a,headers:this.getHeaders()});return await this.handleResponse(e)}catch(e){throw e.name==="AbortError"||console.error("getTimelineStats error:",e),e}}static async getBandwidthSaved(t="all",r){try{const a=await fetch(`${u}/stats/bandwidth-saved?period=${t}`,{signal:r,headers:this.getHeaders()});return await this.handleResponse(a)}catch(a){throw a.name==="AbortError"||console.error("getBandwidthSaved error:",a),a}}static async getTopGames(t=10,r="7d",a){try{const e=await fetch(`${u}/stats/top-games?limit=${t}&period=${r}`,{signal:a,headers:this.getHeaders()});return await this.handleResponse(e)}catch(e){throw e.name==="AbortError"||console.error("getTopGames error:",e),e}}static async clearAllCache(){try{const t=await fetch(`${u}/management/cache/clear-all`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(1e4)});return await this.handleResponse(t)}catch(t){throw console.error("clearAllCache error:",t),t}}static async getCacheClearStatus(t){try{const r=await fetch(`${u}/management/cache/clear-status/${t}`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw console.error("getCacheClearStatus error:",r),r}}static async cancelCacheClear(t){try{const r=await fetch(`${u}/management/cache/clear-cancel/${t}`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(5e3)});return await this.handleResponse(r)}catch(r){throw console.error("cancelCacheClear error:",r),r}}static async getActiveCacheOperations(){try{const t=await fetch(`${u}/management/cache/active-operations`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(t)}catch(t){throw console.error("getActiveCacheOperations error:",t),t}}static async clearCache(t=null){return t?await this.removeServiceFromLogs(t):await this.clearAllCache()}static async resetDatabase(){try{const t=await fetch(`${u}/management/database`,{method:"DELETE",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(6e4)});return await this.handleResponse(t)}catch(t){throw console.error("resetDatabase error:",t),t}}static async resetLogPosition(){try{const t=await fetch(`${u}/management/reset-logs`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(6e4)});return await this.handleResponse(t)}catch(t){throw console.error("resetLogPosition error:",t),t}}static async processAllLogs(){try{const t=await fetch(`${u}/management/process-all-logs`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(12e4)});return await this.handleResponse(t)}catch(t){throw console.error("processAllLogs error:",t),t}}static async cancelProcessing(){try{const t=await fetch(`${u}/management/cancel-processing`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(1e4)});return await this.handleResponse(t)}catch(t){throw console.error("cancelProcessing error:",t),t}}static async getProcessingStatus(){try{const t=await fetch(`${u}/management/processing-status`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(t)}catch(t){throw console.error("getProcessingStatus error:",t),t}}static async removeServiceFromLogs(t){try{const r=await fetch(`${u}/management/logs/remove-service`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),body:JSON.stringify({service:t}),signal:AbortSignal.timeout(12e4)});return await this.handleResponse(r)}catch(r){throw console.error("removeServiceFromLogs error:",r),r}}static async getServiceLogCounts(){try{const t=await fetch(`${u}/management/logs/service-counts`,{signal:AbortSignal.timeout(3e4),headers:this.getHeaders()});return await this.handleResponse(t)}catch(t){throw console.error("getServiceLogCounts error:",t),t}}static async getConfig(){try{const t=await fetch(`${u}/management/config`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(t)}catch(t){return console.error("getConfig error:",t),{cachePath:"/cache",logPath:"/logs/access.log",services:["steam","epic","origin","blizzard","wsus","riot"]}}}}class Q{static generateMockData(t=50){const r=["192.168.1.100","192.168.1.101","192.168.1.102","192.168.1.103","192.168.1.104","192.168.1.105","192.168.1.106","192.168.1.107","192.168.1.108","192.168.1.109","192.168.1.110","192.168.1.111","10.0.0.50","10.0.0.51","10.0.0.52","10.0.0.53","127.0.0.1"],a=[{name:"Counter-Strike 2",size:32212254720},{name:"Dota 2",size:37580963840},{name:"Team Fortress 2",size:26843545600},{name:"Grand Theft Auto V",size:102005473280},{name:"Apex Legends",size:64424509440},{name:"Dead by Daylight",size:48318382080},{name:"Marvel Rivals",size:59055800320},{name:"Path of Exile",size:42949672960},{name:"Warframe",size:53687091200},{name:"Destiny 2",size:112742891520},{name:"Rust",size:37580963840},{name:"Valheim",size:1073741824},{name:"Unknown Steam Game",size:16106127360}],e={totalCacheSize:2e12,usedCacheSize:145e10,freeCacheSize:55e10,usagePercent:72.5,totalFiles:48293+(typeof t=="number"?t:500)*100,serviceSizes:{steam:65e10,epic:32e10,origin:18e10,blizzard:15e10,wsus:1e11,riot:5e10}},s=[],n=new Date,c=t==="unlimited"?500:t,i={};for(let l=0;l<c;l++){const h=N[Math.floor(Math.random()*N.length)],f=r[Math.floor(Math.random()*r.length)],w=Math.random()<.3,k=Math.pow(l/c,2)*2160,g=new Date(n.getTime()-k*60*60*1e3-Math.random()*36e5);let S;if(w){const D=new Date(g.getTime()+Math.random()*5e3);S={id:l+1,service:h,clientIp:f,startTime:g.toISOString(),endTime:D.toISOString(),cacheHitBytes:0,cacheMissBytes:0,totalBytes:0,cacheHitPercent:0,isActive:!1,gameName:null}}else{let D=null,E;if(h==="steam"&&Math.random()<.7){const H=a[Math.floor(Math.random()*a.length)];D=H.name,E=Math.floor(H.size*(.8+Math.random()*.2))}else E=Math.floor(Math.random()*50*1024*1024*1024);const T=Math.min(.95,.1+k/2160*.85),M=Math.floor(E*T),W=E-M,q=T>.8?500*1024*1024:50*1024*1024,F=E/q*1e3,X=new Date(g.getTime()+F);S={id:l+1,service:h,clientIp:f,startTime:g.toISOString(),endTime:X.toISOString(),cacheHitBytes:M,cacheMissBytes:W,totalBytes:E,cacheHitPercent:M/E*100,isActive:l<3&&k<.5,gameName:D,gameAppId:D&&D!=="Unknown Steam Game"?2e5+Math.floor(Math.random()*2e6):null}}i[f]||(i[f]={totalCacheHitBytes:0,totalCacheMissBytes:0,totalDownloads:0,lastSeen:g}),i[f].totalCacheHitBytes+=S.cacheHitBytes||0,i[f].totalCacheMissBytes+=S.cacheMissBytes||0,i[f].totalDownloads+=1,g>i[f].lastSeen&&(i[f].lastSeen=g),s.push(S)}s.sort((l,h)=>new Date(h.startTime).getTime()-new Date(l.startTime).getTime());const p=r.map(l=>{const h=i[l];if(h){const f=h.totalCacheHitBytes+h.totalCacheMissBytes;return{clientIp:l,totalCacheHitBytes:h.totalCacheHitBytes,totalCacheMissBytes:h.totalCacheMissBytes,totalBytes:f,cacheHitPercent:f>0?h.totalCacheHitBytes/f*100:0,totalDownloads:h.totalDownloads,lastSeen:h.lastSeen.toISOString()}}else return{clientIp:l,totalCacheHitBytes:0,totalCacheMissBytes:0,totalBytes:0,cacheHitPercent:0,totalDownloads:0,lastSeen:null}}).filter(l=>l.totalBytes>0),x=N.map(l=>{var k;const h=s.filter(g=>g.service===l),f=h.reduce((g,S)=>g+S.cacheHitBytes,0),w=h.reduce((g,S)=>g+S.cacheMissBytes,0);return{service:l,totalCacheHitBytes:f||e.serviceSizes[l]*.8,totalCacheMissBytes:w||e.serviceSizes[l]*.2,totalBytes:f+w||e.serviceSizes[l],cacheHitPercent:f+w>0?f/(f+w)*100:80,totalDownloads:h.length,lastActivity:((k=h[0])==null?void 0:k.startTime)||new Date(n.getTime()-Math.random()*72e5).toISOString()}});return{cacheInfo:e,activeDownloads:s.filter(l=>l.isActive),latestDownloads:s,clientStats:p,serviceStats:x}}static generateRealtimeUpdate(){const t=["192.168.1.100","192.168.1.101","192.168.1.102","192.168.1.103","192.168.1.104","192.168.1.105","192.168.1.106","192.168.1.107"];if(Math.random()<.2)return{id:Date.now(),service:N[Math.floor(Math.random()*N.length)],clientIp:t[Math.floor(Math.random()*t.length)],startTime:new Date().toISOString(),endTime:new Date().toISOString(),cacheHitBytes:0,cacheMissBytes:0,totalBytes:0,cacheHitPercent:0,isActive:!1};const a=Math.floor(Math.random()*5e8),e=Math.floor(Math.random()*1e8);return{id:Date.now(),service:N[Math.floor(Math.random()*N.length)],clientIp:t[Math.floor(Math.random()*t.length)],startTime:new Date().toISOString(),endTime:null,cacheHitBytes:a,cacheMissBytes:e,totalBytes:a+e,cacheHitPercent:a/(a+e)*100,isActive:!0,gameName:Math.random()<.5?"Counter-Strike 2":null}}}const ee={},oe=d.createContext(void 0),Ye=()=>{const m=d.useContext(oe);if(!m)throw new Error("useData must be used within DataProvider");return m},Ee=({children:m})=>{const[t,r]=d.useState(!1),[a,e]=d.useState(20),[s,n]=d.useState(20),[c,i]=d.useState(null),[p,x]=d.useState([]),[l,h]=d.useState([]),[f,w]=d.useState([]),[k,g]=d.useState([]),[S,D]=d.useState(!0),[E,T]=d.useState(null),[M,W]=d.useState(!1),[q,F]=d.useState(null),[X,H]=d.useState("checking"),O=d.useRef(!0),R=d.useRef(!1),Y=d.useRef(!1),b=d.useRef(null),C=d.useRef(null),se=()=>{if(!(typeof import.meta<"u"&&(ee!=null&&ee.VITE_API_URL)))return""},ne=async()=>{try{const y=se();return(await fetch(`${y}/health`,{signal:AbortSignal.timeout(5e3)})).ok?(H("connected"),!0):(H("error"),!1)}catch{return H("disconnected"),!1}},U=async()=>{if(!(Y.current&&!O.current)){Y.current=!0,b.current&&b.current.abort(),b.current=new AbortController;try{if(O.current&&D(!0),t){const y=a==="unlimited"?100:Math.min(Number(a),100),v=Q.generateMockData(y);i(v.cacheInfo),x(v.activeDownloads),h(v.latestDownloads),w(v.clientStats),g(v.serviceStats),T(null),H("connected"),R.current=!0}else if(await ne())try{const K=setTimeout(()=>{var B;return(B=b.current)==null?void 0:B.abort()},M?3e4:1e4);if(O.current){const[B,A]=await Promise.all([I.getCacheInfo(b.current.signal),I.getActiveDownloads(b.current.signal)]);B&&i(B),A&&x(A);const P=await I.getLatestDownloads(b.current.signal,20);P&&(h(P),R.current=!0),setTimeout(async()=>{var $;if(!(($=b.current)!=null&&$.signal.aborted))try{const[j,_]=await Promise.all([I.getClientStats(b.current.signal),I.getServiceStats(b.current.signal)]);j&&w(j),_&&g(_)}catch{}},100)}else{const B=s==="unlimited"?100:s,[A,P,$,j,_]=await Promise.allSettled([I.getCacheInfo(b.current.signal),I.getActiveDownloads(b.current.signal),I.getLatestDownloads(b.current.signal,B),I.getClientStats(b.current.signal),I.getServiceStats(b.current.signal)]);A.status==="fulfilled"&&A.value!==void 0&&i(A.value),P.status==="fulfilled"&&P.value!==void 0&&x(P.value),$.status==="fulfilled"&&$.value!==void 0&&(h($.value),R.current=!0,M&&$.value.length>0&&F(he=>({...he,message:`Processing logs... Found ${$.value.length} downloads`,downloadCount:$.value.length}))),j.status==="fulfilled"&&j.value!==void 0&&w(j.value),_.status==="fulfilled"&&_.value!==void 0&&g(_.value)}clearTimeout(K),T(null)}catch(v){R.current||(v.name==="AbortError"?T("Request timeout - the server may be busy"):T("Failed to fetch data from API"))}else R.current||T("Cannot connect to API server")}catch(y){console.error("Error in fetchData:",y),!R.current&&!t&&T("An unexpected error occurred")}finally{O.current&&(D(!1),O.current=!1),Y.current=!1}}},ce=y=>{if(t){const v=y==="unlimited"?100:Math.min(y,100);e(v)}},ie=y=>{n(y==="unlimited"?100:y)},te=()=>M?15e3:s==="unlimited"||s>100?3e4:Ae;d.useEffect(()=>{if(!t){U();const y=te();return C.current=setInterval(U,y),()=>{C.current&&(clearInterval(C.current),C.current=null),b.current&&b.current.abort()}}},[M,t,s]),d.useEffect(()=>{if(t){C.current&&(clearInterval(C.current),C.current=null),i(null),x([]),h([]),w([]),g([]);const y=a==="unlimited"?100:Math.min(Number(a),100),v=Q.generateMockData(y);i(v.cacheInfo),x(v.activeDownloads),h(v.latestDownloads),w(v.clientStats),g(v.serviceStats),T(null),H("connected"),R.current=!0;const K=3e4;return C.current=setInterval(()=>{const B=Q.generateRealtimeUpdate();h(A=>[B,...A.slice(0,99)]),x(A=>[B,...A.filter($=>$.id!==B.id)].slice(0,5))},K),()=>{C.current&&(clearInterval(C.current),C.current=null)}}else i(null),x([]),h([]),w([]),g([]),T(null),R.current=!1,O.current=!0,U()},[t,a]),d.useEffect(()=>()=>{C.current&&clearInterval(C.current),b.current&&b.current.abort()},[]);const le={mockMode:t,setMockMode:r,mockDownloadCount:a,updateMockDataCount:ce,apiDownloadCount:s,updateApiDownloadCount:ie,cacheInfo:c,activeDownloads:p,latestDownloads:l,clientStats:f,serviceStats:k,loading:S,error:E,fetchData:U,clearAllData:()=>{i(null),x([]),h([]),w([]),g([]),R.current=!1},isProcessingLogs:M,setIsProcessingLogs:W,processingStatus:q,setProcessingStatus:F,connectionStatus:X,getCurrentRefreshInterval:te};return o.jsx(oe.Provider,{value:le,children:m})},Me=({title:m="LANCache Manager",subtitle:t="High-performance cache monitoring & management",connectionStatus:r="connected"})=>{const e=(()=>{switch(r){case"connected":return{color:"cache-hit",text:"Connected",icon:o.jsx(V,{className:"w-4 h-4"})};case"disconnected":return{color:"text-themed-error",text:"Disconnected",icon:o.jsx(V,{className:"w-4 h-4"})};case"reconnecting":return{color:"cache-miss",text:"Reconnecting...",icon:o.jsx(V,{className:"w-4 h-4 animate-pulse"})};default:return{color:"text-themed-muted",text:"Unknown",icon:o.jsx(V,{className:"w-4 h-4"})}}})();return o.jsx("header",{className:"border-b",style:{backgroundColor:"var(--theme-nav-bg)",borderColor:"var(--theme-nav-border)"},children:o.jsx("div",{className:"container mx-auto px-4",children:o.jsxs("div",{className:"flex items-center justify-between h-16 min-w-0",children:[o.jsxs("div",{className:"flex items-center space-x-2 sm:space-x-3 min-w-0 flex-1",children:[o.jsx("div",{className:"p-1.5 sm:p-2 rounded-lg flex-shrink-0",style:{backgroundColor:"var(--theme-icon-blue)"},children:o.jsx(ue,{className:"w-5 h-5 sm:w-6 sm:h-6 text-white"})}),o.jsxs("div",{className:"min-w-0 flex-1",children:[o.jsx("h1",{className:"text-lg sm:text-xl font-bold text-themed-primary truncate",children:m}),o.jsx("p",{className:"text-xs sm:text-sm text-themed-muted truncate hidden sm:block",children:t})]})]}),o.jsx("div",{className:"flex items-center space-x-1 sm:space-x-2 flex-shrink-0",children:o.jsxs("div",{className:`flex items-center space-x-1 ${e.color}`,children:[e.icon,o.jsx("span",{className:"text-xs sm:text-sm font-medium hidden sm:inline",children:e.text})]})})]})})})},Re=({activeTab:m,setActiveTab:t})=>{var n;const[r,a]=d.useState(!1),e=[{id:"dashboard",label:"Dashboard",icon:fe},{id:"downloads",label:"Downloads",icon:ge},{id:"clients",label:"Clients",icon:be},{id:"management",label:"Management",icon:pe}],s=({tab:c,isActive:i,onClick:p,className:x=""})=>{const l=c.icon;return o.jsxs("button",{onClick:p,className:`flex items-center space-x-2 px-3 py-2 rounded-lg font-medium transition-all duration-200 ${x}`,style:{color:i?"var(--theme-nav-tab-active)":"var(--theme-nav-tab-inactive)",backgroundColor:"transparent"},onMouseEnter:h=>{i||(h.currentTarget.style.color="var(--theme-nav-tab-hover)",h.currentTarget.style.backgroundColor="var(--theme-nav-mobile-item-hover)")},onMouseLeave:h=>{i||(h.currentTarget.style.color="var(--theme-nav-tab-inactive)",h.currentTarget.style.backgroundColor="transparent")},children:[o.jsx(l,{className:"w-5 h-5"}),o.jsx("span",{children:c.label}),i&&o.jsx("div",{className:"absolute bottom-0 left-0 right-0 h-0.5 rounded-full",style:{backgroundColor:"var(--theme-nav-tab-active-border)"}})]})};return o.jsx("nav",{className:"border-b",style:{backgroundColor:"var(--theme-nav-bg)",borderColor:"var(--theme-nav-border)"},children:o.jsxs("div",{className:"container mx-auto px-4",children:[o.jsx("div",{className:"hidden md:flex space-x-1 h-12 items-center",children:e.map(c=>o.jsx("div",{className:"relative",children:o.jsx(s,{tab:c,isActive:m===c.id,onClick:()=>t(c.id)})},c.id))}),o.jsxs("div",{className:"md:hidden",children:[o.jsxs("div",{className:"flex items-center justify-between h-12",children:[o.jsx("div",{className:"flex items-center space-x-2",children:o.jsx("span",{className:"font-medium text-themed-primary",children:((n=e.find(c=>c.id===m))==null?void 0:n.label)||"Dashboard"})}),o.jsx("button",{onClick:()=>a(!r),className:"p-2 rounded-lg transition-colors",style:{color:"var(--theme-nav-tab-inactive)",backgroundColor:"transparent"},onMouseEnter:c=>{c.currentTarget.style.backgroundColor="var(--theme-nav-mobile-item-hover)"},onMouseLeave:c=>{c.currentTarget.style.backgroundColor="transparent"},children:r?o.jsx(ve,{className:"w-5 h-5"}):o.jsx(ye,{className:"w-5 h-5"})})]}),r&&o.jsx("div",{className:"border-t py-2 space-y-1",style:{backgroundColor:"var(--theme-nav-mobile-menu-bg)",borderColor:"var(--theme-nav-border)"},children:e.map(c=>o.jsx(s,{tab:c,isActive:m===c.id,onClick:()=>{t(c.id),a(!1)},className:"w-full justify-start"},c.id))})]})]})})};class ke extends d.Component{constructor(t){super(t),this.state={hasError:!1}}static getDerivedStateFromError(t){return{hasError:!0,error:t}}componentDidCatch(t,r){console.error("Error caught by boundary:",t,r)}render(){var t;return this.state.hasError?o.jsx("div",{className:"min-h-screen bg-themed-primary flex items-center justify-center p-4",children:o.jsx("div",{className:"alert-error rounded-lg p-6 max-w-md w-full",children:o.jsxs("div",{className:"flex items-start space-x-3",children:[o.jsx(xe,{className:"w-5 h-5 mt-0.5"}),o.jsxs("div",{children:[o.jsx("h3",{className:"text-lg font-semibold mb-2",children:"Something went wrong"}),o.jsx("p",{className:"text-sm text-themed-secondary",children:((t=this.state.error)==null?void 0:t.message)||"An unexpected error occurred"}),o.jsx("button",{onClick:()=>window.location.reload(),className:"mt-4 px-4 py-2 action-delete rounded-lg text-sm smooth-transition",children:"Reload Page"})]})]})})}):this.props.children}}const He=({message:m,size:t="md",fullScreen:r=!1})=>{const a={xs:"w-4 h-4",sm:"w-6 h-6",md:"w-8 h-8",lg:"w-12 h-12",xl:"w-16 h-16"},e=o.jsxs("div",{className:"flex flex-col items-center justify-center space-y-4",children:[o.jsx(we,{className:`${a[t]} text-themed-primary animate-spin`}),m&&o.jsx("p",{className:"text-sm text-themed-muted",children:m})]});return r?o.jsx("div",{className:"fixed inset-0 bg-themed-primary bg-opacity-50 flex items-center justify-center z-50",children:e}):o.jsx("div",{className:"flex items-center justify-center min-h-[200px]",children:e})},ae=d.lazy(()=>z(()=>import("./Dashboard-3KyMb8ij.js"),__vite__mapDeps([0,1,2,3,4,5,6]))),Pe=d.lazy(()=>z(()=>import("./DownloadsTab-9z7ll2Na.js"),__vite__mapDeps([7,1,2,3,4,8]))),je=d.lazy(()=>z(()=>import("./ClientsTab-DcfTH012.js"),__vite__mapDeps([9,1,2,3,4,6]))),Ne=d.lazy(()=>z(()=>import("./ServicesTab-DJIVowUo.js"),__vite__mapDeps([10,1,2,3,4,6]))),Oe=d.lazy(()=>z(()=>import("./ManagementTab-Cga9Y9aa.js"),__vite__mapDeps([11,1,2,3,4,8,12]))),_e=()=>{const[m,t]=d.useState("dashboard"),r=()=>{const a=(()=>{switch(m){case"dashboard":return ae;case"downloads":return Pe;case"clients":return je;case"services":return Ne;case"management":return Oe;default:return ae}})();return o.jsx(d.Suspense,{fallback:o.jsx(He,{fullScreen:!1,message:"Loading..."}),children:o.jsx(a,{})})};return o.jsx(ke,{children:o.jsx(Ee,{children:o.jsxs("div",{className:"min-h-screen",style:{backgroundColor:"var(--theme-bg-primary)",color:"var(--theme-text-primary)"},children:[o.jsx(Me,{}),o.jsx(Re,{activeTab:m,setActiveTab:t}),o.jsx("main",{className:"container mx-auto px-4 py-6",children:r()})]})})})};class Le{constructor(){L(this,"currentTheme",null);L(this,"styleElement",null)}getContrastText(t){return!t||t==="transparent"?"var(--theme-text-primary)":getComputedStyle(document.documentElement).getPropertyValue("--theme-button-text").trim()||"#ffffff"}async loadThemes(){const t=this.getBuiltInThemes(),r=[],a=[];try{const n=await fetch(`${u}/theme`);if(n.ok){const c=await n.json();for(const i of c)if(i.format==="toml")try{const p=await fetch(`${u}/theme/${i.id}`);if(p.status===404){a.push(i.id);continue}if(p.ok){const x=await p.text(),l=this.parseTomlTheme(x);l&&r.push(l)}}catch(p){console.error(`Failed to load theme ${i.id}:`,p)}if(a.length>0&&this.currentTheme&&a.includes(this.currentTheme.meta.id)){const i=t.find(p=>p.meta.id==="dark-default");i&&this.applyTheme(i)}}}catch(n){console.error("Failed to load themes from server:",n)}const e=[...t],s=new Set(e.map(n=>n.meta.id));return r.forEach(n=>{s.has(n.meta.id)||(e.push(n),s.add(n.meta.id))}),e}getBuiltInThemes(){return[{meta:{id:"dark-default",name:"Dark Default",description:"Default dark theme with blue accents",author:"System",version:"1.0.0",isDark:!0},colors:{primaryColor:"#3b82f6",secondaryColor:"#8b5cf6",accentColor:"#06b6d4",bgPrimary:"#111827",bgSecondary:"#1f2937",bgTertiary:"#374151",bgHover:"#4b5563",textPrimary:"#ffffff",textSecondary:"#d1d5db",textMuted:"#9ca3af",textAccent:"#60a5fa",textPlaceholder:"#6b7280",dragHandleColor:"#6b7280",dragHandleHover:"#60a5fa",borderPrimary:"#374151",borderSecondary:"#4b5563",borderFocus:"#3b82f6",navBg:"#1f2937",navBorder:"#374151",navTabActive:"#3b82f6",navTabInactive:"#9ca3af",navTabHover:"#ffffff",navTabActiveBorder:"#3b82f6",navMobileMenuBg:"#1f2937",navMobileItemHover:"#374151",success:"#10b981",successBg:"#064e3b",successText:"#34d399",warning:"#fb923c",warningBg:"#44403c",warningText:"#fcd34d",error:"#ef4444",errorBg:"#7f1d1d",errorText:"#fca5a5",info:"#3b82f6",infoBg:"#1e3a8a",infoText:"#93c5fd",steamColor:"#3b82f6",epicColor:"#8b5cf6",originColor:"#10b981",blizzardColor:"#ef4444",wsusColor:"#06b6d4",riotColor:"#f59e0b",cardBg:"#1f2937",cardBorder:"#374151",buttonBg:"#3b82f6",buttonHover:"#2563eb",buttonText:"#ffffff",inputBg:"#374151",inputBorder:"#4b5563",inputFocus:"#3b82f6",badgeBg:"#3b82f6",badgeText:"#ffffff",progressBar:"#3b82f6",progressBg:"#374151",hitRateHighBg:"#064e3b",hitRateHighText:"#34d399",hitRateMediumBg:"#1e3a8a",hitRateMediumText:"#93c5fd",hitRateLowBg:"#44403c",hitRateLowText:"#fbbf24",hitRateWarningBg:"#44403c",hitRateWarningText:"#fcd34d",actionResetBg:"#f59e0b",actionResetHover:"#d97706",actionProcessBg:"#10b981",actionProcessHover:"#059669",actionDeleteBg:"#ef4444",actionDeleteHover:"#dc2626",iconBgBlue:"#3b82f6",iconBgGreen:"#10b981",iconBgEmerald:"#10b981",iconBgPurple:"#8b5cf6",iconBgIndigo:"#6366f1",iconBgOrange:"#f97316",iconBgYellow:"#eab308",iconBgCyan:"#06b6d4",iconBgRed:"#ef4444",chartColor1:"#3b82f6",chartColor2:"#10b981",chartColor3:"#f59e0b",chartColor4:"#ef4444",chartColor5:"#8b5cf6",chartColor6:"#06b6d4",chartColor7:"#f97316",chartColor8:"#ec4899",chartBorderColor:"#1f2937",chartGridColor:"#374151",chartTextColor:"#9ca3af",chartCacheHitColor:"#10b981",chartCacheMissColor:"#f59e0b",scrollbarTrack:"#374151",scrollbarThumb:"#6B7280",scrollbarHover:"#9CA3AF",publicAccessBg:"rgba(16, 185, 129, 0.2)",publicAccessText:"#34d399",publicAccessBorder:"rgba(16, 185, 129, 0.3)",securedAccessBg:"rgba(245, 158, 11, 0.2)",securedAccessText:"#fbbf24",securedAccessBorder:"rgba(245, 158, 11, 0.3)"}},{meta:{id:"light-default",name:"Light Default",description:"Default light theme with blue accents",author:"System",version:"1.0.0",isDark:!1},colors:{primaryColor:"#3b82f6",secondaryColor:"#8b5cf6",accentColor:"#06b6d4",bgPrimary:"#f8f9fa",bgSecondary:"#ffffff",bgTertiary:"#f3f4f6",bgHover:"#e5e7eb",textPrimary:"#111827",textSecondary:"#374151",textMuted:"#6b7280",textAccent:"#2563eb",textPlaceholder:"#9ca3af",dragHandleColor:"#9ca3af",dragHandleHover:"#2563eb",borderPrimary:"#e5e7eb",borderSecondary:"#d1d5db",borderFocus:"#3b82f6",navBg:"#ffffff",navBorder:"#e5e7eb",navTabActive:"#3b82f6",navTabInactive:"#6b7280",navTabHover:"#111827",navTabActiveBorder:"#3b82f6",navMobileMenuBg:"#ffffff",navMobileItemHover:"#f3f4f6",success:"#10b981",successBg:"#d1fae5",successText:"#047857",warning:"#f97316",warningBg:"#fef3c7",warningText:"#b45309",error:"#ef4444",errorBg:"#fee2e2",errorText:"#991b1b",info:"#3b82f6",infoBg:"#dbeafe",infoText:"#1e40af",steamColor:"#3b82f6",epicColor:"#8b5cf6",originColor:"#10b981",blizzardColor:"#ef4444",wsusColor:"#06b6d4",riotColor:"#f59e0b",cardBg:"#ffffff",cardBorder:"#e5e7eb",buttonBg:"#3b82f6",buttonHover:"#2563eb",buttonText:"#ffffff",inputBg:"#ffffff",inputBorder:"#d1d5db",inputFocus:"#3b82f6",badgeBg:"#3b82f6",badgeText:"#ffffff",progressBar:"#3b82f6",progressBg:"#e5e7eb",hitRateHighBg:"#d1fae5",hitRateHighText:"#047857",hitRateMediumBg:"#dbeafe",hitRateMediumText:"#1e40af",hitRateLowBg:"#fef3c7",hitRateLowText:"#92400e",hitRateWarningBg:"#fef3c7",hitRateWarningText:"#92400e",actionResetBg:"#f59e0b",actionResetHover:"#d97706",actionProcessBg:"#10b981",actionProcessHover:"#059669",actionDeleteBg:"#ef4444",actionDeleteHover:"#dc2626",iconBgBlue:"#3b82f6",iconBgGreen:"#10b981",iconBgEmerald:"#10b981",iconBgPurple:"#8b5cf6",iconBgIndigo:"#6366f1",iconBgOrange:"#f97316",iconBgYellow:"#eab308",iconBgCyan:"#06b6d4",iconBgRed:"#ef4444",chartColor1:"#3b82f6",chartColor2:"#10b981",chartColor3:"#f59e0b",chartColor4:"#ef4444",chartColor5:"#8b5cf6",chartColor6:"#06b6d4",chartColor7:"#f97316",chartColor8:"#ec4899",chartBorderColor:"#e5e7eb",chartGridColor:"#d1d5db",chartTextColor:"#6b7280",chartCacheHitColor:"#047857",chartCacheMissColor:"#b45309",scrollbarTrack:"#e5e7eb",scrollbarThumb:"#9ca3af",scrollbarHover:"#6b7280",publicAccessBg:"#d1fae5",publicAccessText:"#047857",publicAccessBorder:"#86efac",securedAccessBg:"#fef3c7",securedAccessText:"#92400e",securedAccessBorder:"#fde047"}}]}async getTheme(t){const r=this.getBuiltInThemes().find(a=>a.meta.id===t);if(r)return r;try{const a=await fetch(`${u}/theme/${t}`);if(!a.ok)return null;const e=await a.text();return this.parseTomlTheme(e)}catch(a){return console.error("Error loading theme:",a),null}}parseTomlTheme(t){try{const r=Te.parse(t);return!r.meta||!r.meta.id||!r.meta.name?(console.error("Invalid theme: missing meta.id or meta.name"),null):r.colors?r:(console.error("Invalid theme: missing colors section"),null)}catch(r){return console.error("Error parsing TOML theme:",r),null}}async uploadTheme(t){const r=await t.text(),a=this.parseTomlTheme(r);if(!a)throw new Error("Invalid TOML theme format");const e=new FormData;e.append("file",t);try{const s=await fetch(`${u}/theme/upload`,{method:"POST",headers:G.getAuthHeaders(),body:e});if(!s.ok){const n=await s.json().catch(()=>({error:"Failed to upload theme"}));throw new Error(n.error||"Failed to upload theme")}return a}catch(s){throw s.message.includes("Failed to fetch")||s.message.includes("NetworkError")?new Error("Cannot save theme: API server is not running. Please start the LANCache Manager API service."):s}}async deleteTheme(t){const r=await fetch(`${u}/theme/${t}`,{method:"DELETE",headers:G.getAuthHeaders()});if(!r.ok&&r.status!==404){const a=await r.json().catch(()=>({error:"Failed to delete theme"}));throw new Error(a.error||"Failed to delete theme")}}applyDefaultVariables(){const t=`
      :root {
        --theme-primary: #3b82f6;
        --theme-secondary: #8b5cf6;
        --theme-accent: #06b6d4;
        --theme-bg-primary: #111827;
        --theme-bg-secondary: #1f2937;
        --theme-bg-tertiary: #374151;
        --theme-bg-hover: #4b5563;
        --theme-text-primary: #ffffff;
        --theme-text-secondary: #d1d5db;
        --theme-text-muted: #9ca3af;
        --theme-text-accent: #60a5fa;
        --theme-drag-handle: #6b7280;
        --theme-drag-handle-hover: #60a5fa;
        --theme-border-primary: #374151;
        --theme-border-secondary: #4b5563;
        --theme-border-accent: #06b6d4;
        --theme-border-subtle: rgba(255, 255, 255, 0.1);
        --theme-border-focus: #3b82f6;
        --theme-nav-bg: #1f2937;
        --theme-nav-border: #374151;
        --theme-nav-tab-active: #3b82f6;
        --theme-nav-tab-inactive: #9ca3af;
        --theme-nav-tab-hover: #ffffff;
        --theme-nav-tab-active-border: #3b82f6;
        --theme-nav-mobile-menu-bg: #1f2937;
        --theme-nav-mobile-item-hover: #374151;
        --theme-success: #10b981;
        --theme-success-bg: #064e3b;
        --theme-success-text: #34d399;
        --theme-warning: #f59e0b;
        --theme-warning-bg: #78350f;
        --theme-warning-text: #fbbf24;
        --theme-error: #ef4444;
        --theme-error-bg: #7f1d1d;
        --theme-error-text: #fca5a5;
        --theme-info: #3b82f6;
        --theme-info-bg: #1e3a8a;
        --theme-info-text: #93c5fd;
        --theme-steam: #3b82f6;
        --theme-epic: #8b5cf6;
        --theme-origin: #10b981;
        --theme-blizzard: #ef4444;
        --theme-wsus: #06b6d4;
        --theme-riot: #f59e0b;
        --theme-card-bg: #1f2937;
        --theme-card-border: #374151;
        --theme-button-bg: #3b82f6;
        --theme-button-hover: #2563eb;
        --theme-button-text: #ffffff;
        --theme-input-bg: #374151;
        --theme-input-border: #4b5563;
        --theme-input-focus: #3b82f6;
        --theme-checkbox-accent: #3b82f6;
        --theme-checkbox-border: #4b5563;
        --theme-slider-accent: #3b82f6;
        --theme-slider-thumb: #3b82f6;
        --theme-slider-track: #374151;
        --theme-badge-bg: #3b82f6;
        --theme-badge-text: #ffffff;
        --theme-progress-bar: #3b82f6;
        --theme-progress-bg: #374151;
        --theme-hit-rate-high-bg: #064e3b;
        --theme-hit-rate-high-text: #34d399;
        --theme-hit-rate-medium-bg: #1e3a8a;
        --theme-hit-rate-medium-text: #93c5fd;
        --theme-hit-rate-low-bg: #ea580c;
        --theme-hit-rate-low-text: #fb923c;
        --theme-hit-rate-warning-bg: #78350f;
        --theme-hit-rate-warning-text: #fbbf24;
        --theme-action-reset-bg: #f59e0b;
        --theme-action-reset-hover: #d97706;
        --theme-action-process-bg: #10b981;
        --theme-action-process-hover: #059669;
        --theme-action-delete-bg: #ef4444;
        --theme-action-delete-hover: #dc2626;
        --theme-icon-blue: #3b82f6;
        --theme-icon-green: #10b981;
        --theme-icon-emerald: #10b981;
        --theme-icon-purple: #8b5cf6;
        --theme-icon-indigo: #6366f1;
        --theme-icon-orange: #f97316;
        --theme-icon-yellow: #eab308;
        --theme-icon-cyan: #06b6d4;
        --theme-icon-red: #ef4444;
        --theme-chart-1: #3b82f6;
        --theme-chart-2: #10b981;
        --theme-chart-3: #f59e0b;
        --theme-chart-4: #ef4444;
        --theme-chart-5: #8b5cf6;
        --theme-chart-6: #06b6d4;
        --theme-chart-7: #f97316;
        --theme-chart-8: #ec4899;
        --theme-chart-border: #1f2937;
        --theme-chart-grid: #374151;
        --theme-chart-text: #9ca3af;
        --theme-chart-cache-hit: #10b981;
        --theme-chart-cache-miss: #f59e0b;
      }
    `;let r=document.getElementById("lancache-default-vars");r||(r=document.createElement("style"),r.id="lancache-default-vars",document.head.appendChild(r)),r.textContent=t}clearTheme(){this.styleElement&&(this.styleElement.remove(),this.styleElement=null);const t=document.documentElement;t.removeAttribute("data-theme"),t.removeAttribute("data-theme-id"),this.currentTheme=null,localStorage.removeItem("lancache_selected_theme"),localStorage.removeItem("lancache_theme_css"),localStorage.removeItem("lancache_theme_dark"),this.applyDefaultVariables()}applyTheme(t){var c;if(!t||!t.colors)return;this.styleElement&&(this.styleElement.remove(),this.styleElement=null);const r=document.getElementById("lancache-theme-preload");r&&r.remove();const a=document.getElementById("lancache-default-preload");a&&a.remove();const e=t.colors,s=`
    :root {
      --theme-primary: ${e.primaryColor||"#3b82f6"};
      --theme-secondary: ${e.secondaryColor||"#8b5cf6"};
      --theme-accent: ${e.accentColor||"#06b6d4"};
      --theme-bg-primary: ${e.bgPrimary||"#111827"};
      --theme-bg-secondary: ${e.bgSecondary||"#1f2937"};
      --theme-bg-tertiary: ${e.bgTertiary||"#374151"};
      --theme-bg-hover: ${e.bgHover||"#4b5563"};
      --theme-text-primary: ${e.textPrimary||"#ffffff"};
      --theme-text-secondary: ${e.textSecondary||"#d1d5db"};
      --theme-text-muted: ${e.textMuted||"#9ca3af"};
      --theme-text-accent: ${e.textAccent||"#60a5fa"};
      --theme-text-placeholder: ${e.textPlaceholder||"#6b7280"};
      --theme-drag-handle: ${e.dragHandleColor||e.textMuted||"#6b7280"};
      --theme-drag-handle-hover: ${e.dragHandleHover||e.textAccent||"#60a5fa"};
      --theme-border-primary: ${e.borderPrimary||"#374151"};
      --theme-border-secondary: ${e.borderSecondary||"#4b5563"};
      --theme-border-accent: ${e.borderAccent||e.accentColor||"#06b6d4"};
      --theme-border-subtle: ${e.borderSubtle||"rgba(255, 255, 255, 0.1)"};
      --theme-border-focus: ${e.borderFocus||"#3b82f6"};
      
      /* Navigation Variables */
      --theme-nav-bg: ${e.navBg||e.bgSecondary||"#1f2937"};
      --theme-nav-border: ${e.navBorder||e.borderPrimary||"#374151"};
      --theme-nav-tab-active: ${e.navTabActive||e.primaryColor||"#3b82f6"};
      --theme-nav-tab-inactive: ${e.navTabInactive||e.textMuted||"#9ca3af"};
      --theme-nav-tab-hover: ${e.navTabHover||e.textPrimary||"#ffffff"};
      --theme-nav-tab-active-border: ${e.navTabActiveBorder||e.primaryColor||"#3b82f6"};
      --theme-nav-mobile-menu-bg: ${e.navMobileMenuBg||e.bgSecondary||"#1f2937"};
      --theme-nav-mobile-item-hover: ${e.navMobileItemHover||e.bgTertiary||"#374151"};
      
      /* Status Colors */
      --theme-success: ${e.success||"#10b981"};
      --theme-success-bg: ${e.successBg||"#064e3b"};
      --theme-success-text: ${e.successText||"#34d399"};
      --theme-warning: ${e.warning||"#f59e0b"};
      --theme-warning-bg: ${e.warningBg||"#78350f"};
      --theme-warning-text: ${e.warningText||"#fbbf24"};
      --theme-error: ${e.error||"#ef4444"};
      --theme-error-bg: ${e.errorBg||"#7f1d1d"};
      --theme-error-text: ${e.errorText||"#fca5a5"};
      --theme-info: ${e.info||"#3b82f6"};
      --theme-info-bg: ${e.infoBg||"#1e3a8a"};
      --theme-info-text: ${e.infoText||"#93c5fd"};
      
      /* Service Colors */
      --theme-steam: ${e.steamColor||"#3b82f6"};
      --theme-epic: ${e.epicColor||"#8b5cf6"};
      --theme-origin: ${e.originColor||"#10b981"};
      --theme-blizzard: ${e.blizzardColor||"#ef4444"};
      --theme-wsus: ${e.wsusColor||"#06b6d4"};
      --theme-riot: ${e.riotColor||"#f59e0b"};
      
      /* Component Colors */
      --theme-card-bg: ${e.cardBg||e.bgSecondary||"#1f2937"};
      --theme-card-border: ${e.cardBorder||e.borderPrimary||"#374151"};
      --theme-button-bg: ${e.buttonBg||e.primaryColor||"#3b82f6"};
      --theme-button-hover: ${e.buttonHover||"#2563eb"};
      --theme-button-text: ${e.buttonText||"#ffffff"};
      --theme-input-bg: ${e.inputBg||e.bgTertiary||"#374151"};
      --theme-input-border: ${e.inputBorder||e.borderSecondary||"#4b5563"};
      --theme-input-focus: ${e.inputFocus||e.primaryColor||"#3b82f6"};
      --theme-checkbox-accent: ${e.checkboxAccent||e.primaryColor||"#3b82f6"};
      --theme-checkbox-border: ${e.checkboxBorder||e.borderSecondary||"#4b5563"};
      --theme-slider-accent: ${e.sliderAccent||e.primaryColor||"#3b82f6"};
      --theme-slider-thumb: ${e.sliderThumb||e.primaryColor||"#3b82f6"};
      --theme-slider-track: ${e.sliderTrack||e.bgTertiary||"#374151"};
      --theme-badge-bg: ${e.badgeBg||e.primaryColor||"#3b82f6"};
      --theme-badge-text: ${e.badgeText||"#ffffff"};
      --theme-progress-bar: ${e.progressBar||e.primaryColor||"#3b82f6"};
      --theme-progress-bg: ${e.progressBg||e.bgTertiary||"#374151"};
      
      /* Hit Rate Colors - FIXED WITH PRETTIER COLORS */
      --theme-hit-rate-high-bg: ${e.hitRateHighBg||"#064e3b"};
      --theme-hit-rate-high-text: ${e.hitRateHighText||"#34d399"};
      --theme-hit-rate-medium-bg: ${e.hitRateMediumBg||"#1e3a8a"};
      --theme-hit-rate-medium-text: ${e.hitRateMediumText||"#93c5fd"};
      --theme-hit-rate-low-bg: ${e.hitRateLowBg||"#44403c"};
      --theme-hit-rate-low-text: ${e.hitRateLowText||"#fbbf24"};
      --theme-hit-rate-warning-bg: ${e.hitRateWarningBg||"#44403c"};
      --theme-hit-rate-warning-text: ${e.hitRateWarningText||"#fcd34d"};
      
      /* Action Button Colors */
      --theme-action-reset-bg: ${e.actionResetBg||"#f59e0b"};
      --theme-action-reset-hover: ${e.actionResetHover||"#d97706"};
      --theme-action-process-bg: ${e.actionProcessBg||"#10b981"};
      --theme-action-process-hover: ${e.actionProcessHover||"#059669"};
      --theme-action-delete-bg: ${e.actionDeleteBg||"#ef4444"};
      --theme-action-delete-hover: ${e.actionDeleteHover||"#dc2626"};
      
      /* Icon Colors */
      --theme-icon-blue: ${e.iconBgBlue||"#3b82f6"};
      --theme-icon-green: ${e.iconBgGreen||"#10b981"};
      --theme-icon-emerald: ${e.iconBgEmerald||"#10b981"};
      --theme-icon-purple: ${e.iconBgPurple||"#8b5cf6"};
      --theme-icon-indigo: ${e.iconBgIndigo||"#6366f1"};
      --theme-icon-orange: ${e.iconBgOrange||"#f97316"};
      --theme-icon-yellow: ${e.iconBgYellow||"#eab308"};
      --theme-icon-cyan: ${e.iconBgCyan||"#06b6d4"};
      --theme-icon-red: ${e.iconBgRed||"#ef4444"};
      
      /* Chart Colors */
      --theme-chart-1: ${e.chartColor1||"#3b82f6"};
      --theme-chart-2: ${e.chartColor2||"#10b981"};
      --theme-chart-3: ${e.chartColor3||"#f59e0b"};
      --theme-chart-4: ${e.chartColor4||"#ef4444"};
      --theme-chart-5: ${e.chartColor5||"#8b5cf6"};
      --theme-chart-6: ${e.chartColor6||"#06b6d4"};
      --theme-chart-7: ${e.chartColor7||"#f97316"};
      --theme-chart-8: ${e.chartColor8||"#ec4899"};
      --theme-chart-border: ${e.chartBorderColor||"#1f2937"};
      --theme-chart-grid: ${e.chartGridColor||"#374151"};
      --theme-chart-text: ${e.chartTextColor||"#9ca3af"};
      --theme-chart-cache-hit: ${e.chartCacheHitColor||"#10b981"};
      --theme-chart-cache-miss: ${e.chartCacheMissColor||"#f59e0b"};
      
      /* Scrollbar Colors */
      --theme-scrollbar-track: ${e.scrollbarTrack||e.bgTertiary||"#374151"};
      --theme-scrollbar-thumb: ${e.scrollbarThumb||e.textMuted||"#6B7280"};
      --theme-scrollbar-hover: ${e.scrollbarHover||e.textSecondary||"#9CA3AF"};

      /* Access Indicator Colors */
      --theme-public-access-bg: ${e.publicAccessBg||"rgba(16, 185, 129, 0.2)"};
      --theme-public-access-text: ${e.publicAccessText||"#34d399"};
      --theme-public-access-border: ${e.publicAccessBorder||"rgba(16, 185, 129, 0.3)"};
      --theme-secured-access-bg: ${e.securedAccessBg||"rgba(245, 158, 11, 0.2)"};
      --theme-secured-access-text: ${e.securedAccessText||"#fbbf24"};
      --theme-secured-access-border: ${e.securedAccessBorder||"rgba(245, 158, 11, 0.3)"};
    }

    /* Global Transitions */
    body * {
      transition: background-color 0.2s ease, color 0.2s ease, border-color 0.2s ease;
    }

    /* Global Body Style */
    body {
      background-color: var(--theme-bg-primary) !important;
      color: var(--theme-text-primary) !important;
    }

    /* Custom CSS from theme */
    ${((c=t.css)==null?void 0:c.content)||""}
  `;this.styleElement=document.createElement("style"),this.styleElement.id="lancache-theme",this.styleElement.textContent=s,document.head.appendChild(this.styleElement);const n=document.documentElement;n.setAttribute("data-theme",t.meta.isDark?"dark":"light"),n.setAttribute("data-theme-id",t.meta.id),this.currentTheme=t,localStorage.setItem("lancache_selected_theme",t.meta.id),localStorage.setItem("lancache_theme_css",s),localStorage.setItem("lancache_theme_dark",t.meta.isDark?"true":"false"),window.dispatchEvent(new Event("themechange"))}async loadSavedTheme(){const t=document.getElementById("lancache-theme-preload"),r=localStorage.getItem("lancache_selected_theme");if(t&&r){const e=await this.getTheme(r);if(e){this.applyTheme(e),this.currentTheme=e;return}localStorage.removeItem("lancache_selected_theme"),localStorage.removeItem("lancache_theme_css"),localStorage.removeItem("lancache_theme_dark")}if(this.applyDefaultVariables(),r){const e=await this.getTheme(r);if(e){this.applyTheme(e);return}}const a=await this.getTheme("dark-default");a&&this.applyTheme(a)}getCurrentThemeId(){var t;return((t=this.currentTheme)==null?void 0:t.meta.id)||"dark-default"}getCurrentTheme(){return this.currentTheme}isThemeApplied(){return this.currentTheme!==null}exportTheme(t){var a;let r="";return r+=`[meta]
`,r+=`name = "${t.meta.name}"
`,r+=`id = "${t.meta.id}"
`,t.meta.description&&(r+=`description = "${t.meta.description}"
`),t.meta.author&&(r+=`author = "${t.meta.author}"
`),t.meta.version&&(r+=`version = "${t.meta.version}"
`),t.meta.isDark!==void 0&&(r+=`isDark = ${t.meta.isDark}
`),r+=`
`,r+=`[colors]
`,t.colors&&Object.entries(t.colors).forEach(([e,s])=>{r+=`${e} = "${s}"
`}),r+=`
`,t.custom&&Object.keys(t.custom).length>0&&(r+=`[custom]
`,Object.entries(t.custom).forEach(([e,s])=>{r+=`"${e}" = "${s}"
`}),r+=`
`),(a=t.css)!=null&&a.content&&(r+=`[css]
`,r+=`content = """
${t.css.content}
"""
`),r}}const ze=new Le;ze.loadSavedTheme();Ce.createRoot(document.getElementById("root")).render(o.jsx(Se.StrictMode,{children:o.jsx(_e,{})}));export{I as A,qe as F,Xe as S,G as a,u as b,ze as t,Ye as u};
//# sourceMappingURL=index-BcacPOWb.js.map
