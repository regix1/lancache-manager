const __vite__mapDeps=(i,m=__vite__mapDeps,d=(m.f||(m.f=["assets/Dashboard-B7wxeKTI.js","assets/react-vendor-DGr9LWon.js","assets/vendor-BmYUbuLR.js","assets/tanstack-CPlEqbpz.js","assets/Card-BNk8Xjj9.js","assets/charts-B1PwCyAI.js","assets/Tooltip-BasvYt1I.js","assets/DownloadsTab-D9kbXAvS.js","assets/Alert-XuuEtZ24.js","assets/ClientsTab-eDHaghpV.js","assets/ServicesTab-C2Ksvi4a.js","assets/ManagementTab-BX7Gk_CV.js","assets/signalr-C7Jyn4vH.js"])))=>i.map(i=>d[i]);
var de=Object.defineProperty;var me=(m,t,e)=>t in m?de(m,t,{enumerable:!0,configurable:!0,writable:!0,value:e}):m[t]=e;var _=(m,t,e)=>me(m,typeof t!="symbol"?t+"":t,e);import{r as d,j as o,M as ue,W as q,L as fe,D as ge,U as be,S as pe,X as ve,a as ye,C as xe,b as we,c as Ce,R as Se}from"./react-vendor-DGr9LWon.js";import{t as Te}from"./vendor-BmYUbuLR.js";import"./tanstack-CPlEqbpz.js";(function(){const t=document.createElement("link").relList;if(t&&t.supports&&t.supports("modulepreload"))return;for(const a of document.querySelectorAll('link[rel="modulepreload"]'))r(a);new MutationObserver(a=>{for(const s of a)if(s.type==="childList")for(const i of s.addedNodes)i.tagName==="LINK"&&i.rel==="modulepreload"&&r(i)}).observe(document,{childList:!0,subtree:!0});function e(a){const s={};return a.integrity&&(s.integrity=a.integrity),a.referrerPolicy&&(s.referrerPolicy=a.referrerPolicy),a.crossOrigin==="use-credentials"?s.credentials="include":a.crossOrigin==="anonymous"?s.credentials="omit":s.credentials="same-origin",s}function r(a){if(a.ep)return;a.ep=!0;const s=e(a);fetch(a.href,s)}})();const Be="modulepreload",$e=function(m){return"/"+m},re={},z=function(t,e,r){let a=Promise.resolve();if(e&&e.length>0){document.getElementsByTagName("link");const i=document.querySelector("meta[property=csp-nonce]"),h=(i==null?void 0:i.nonce)||(i==null?void 0:i.getAttribute("nonce"));a=Promise.allSettled(e.map(n=>{if(n=$e(n),n in re)return;re[n]=!0;const b=n.endsWith(".css"),v=b?'[rel="stylesheet"]':"";if(document.querySelector(`link[href="${n}"]${v}`))return;const c=document.createElement("link");if(c.rel=b?"stylesheet":Be,b||(c.as="script"),c.crossOrigin="",c.href=n,h&&c.setAttribute("nonce",h),document.head.appendChild(c),b)return new Promise((l,f)=>{c.addEventListener("load",l),c.addEventListener("error",()=>f(new Error(`Unable to preload CSS for ${n}`)))})}))}function s(i){const h=new Event("vite:preloadError",{cancelable:!0});if(h.payload=i,window.dispatchEvent(h),!h.defaultPrevented)throw i}return a.then(i=>{for(const h of i||[])h.status==="rejected"&&s(h.reason);return t().catch(s)})},u="/api",N=["steam","epic","origin","blizzard","wsus","riot"],Ae=5e3,We=["B","KB","MB","GB","TB","PB"],Xe={DASHBOARD_CARD_ORDER:"lancache_dashboard_card_order",DASHBOARD_CARD_VISIBILITY:"lancache_dashboard_card_visibility"},J={},Ie=()=>{if(!(typeof import.meta<"u"&&(J!=null&&J.VITE_API_URL)))return""},Z=Ie();class De{constructor(){_(this,"deviceId");_(this,"isAuthenticated");_(this,"authChecked");this.deviceId=this.getOrCreateDeviceId(),this.isAuthenticated=!1,this.authChecked=!1}getOrCreateDeviceId(){let t=localStorage.getItem("lancache_device_id");return t||(t="xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g,function(e){const r=Math.random()*16|0;return(e==="x"?r:r&3|8).toString(16)}),localStorage.setItem("lancache_device_id",t)),t}async checkAuth(){try{const t=await fetch(`${Z}/api/auth/check`,{headers:{"X-Device-Id":this.deviceId}});if(t.ok){const e=await t.json();return this.isAuthenticated=e.isAuthenticated,this.authChecked=!0,e}return this.isAuthenticated=!1,this.authChecked=!0,{requiresAuth:!0,isAuthenticated:!1}}catch(t){return console.error("Auth check failed:",t),this.authChecked=!0,{requiresAuth:!1,isAuthenticated:!1,error:t.message}}}async register(t,e=null){try{const r=await fetch(`${Z}/api/auth/register`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({deviceId:this.deviceId,apiKey:t,deviceName:e||this.getDeviceName()})}),a=await r.json();return r.ok&&a.success?(this.isAuthenticated=!0,localStorage.setItem("lancache_auth_registered","true"),{success:!0,message:a.message}):{success:!1,message:a.message||"Registration failed"}}catch(r){return console.error("Registration failed:",r),{success:!1,message:r.message||"Network error during registration"}}}async regenerateApiKey(){try{const t=await fetch(`${Z}/api/auth/regenerate-key`,{method:"POST",headers:{"Content-Type":"application/json","X-Device-Id":this.deviceId}}),e=await t.json();return t.ok&&e.success?(this.clearAuth(),this.isAuthenticated=!1,{success:!0,message:e.message,warning:e.warning}):{success:!1,message:e.message||"Failed to regenerate API key"}}catch(t){return console.error("Failed to regenerate API key:",t),{success:!1,message:t.message||"Network error while regenerating API key"}}}getDeviceName(){const t=navigator.userAgent;let e="Unknown OS",r="Unknown Browser";return t.indexOf("Win")!==-1?e="Windows":t.indexOf("Mac")!==-1?e="macOS":t.indexOf("Linux")!==-1?e="Linux":t.indexOf("Android")!==-1?e="Android":t.indexOf("iOS")!==-1&&(e="iOS"),t.indexOf("Chrome")!==-1?r="Chrome":t.indexOf("Safari")!==-1?r="Safari":t.indexOf("Firefox")!==-1?r="Firefox":t.indexOf("Edge")!==-1&&(r="Edge"),`${r} on ${e}`}getAuthHeaders(){return{"X-Device-Id":this.deviceId}}handleUnauthorized(){this.isAuthenticated=!1,localStorage.removeItem("lancache_auth_registered")}clearAuth(){this.isAuthenticated=!1,localStorage.removeItem("lancache_auth_registered")}isRegistered(){return localStorage.getItem("lancache_auth_registered")==="true"}}const G=new De;class I{static async handleResponse(t){if(t.status===401){G.handleUnauthorized();const e=await t.text().catch(()=>"");throw new Error(`Authentication required: ${e||"Please provide API key"}`)}if(!t.ok){const e=await t.text().catch(()=>"");throw new Error(`HTTP ${t.status}: ${e||t.statusText}`)}return t.json()}static getHeaders(t={}){return{...G.getAuthHeaders(),...t}}static async getCacheInfo(t){try{const e=await fetch(`${u}/management/cache`,{signal:t,headers:this.getHeaders()});return await this.handleResponse(e)}catch(e){throw e.name==="AbortError"?console.log("getCacheInfo request aborted (timeout)"):console.error("getCacheInfo error:",e),e}}static async getActiveDownloads(t){try{const e=await fetch(`${u}/downloads/active`,{signal:t,headers:this.getHeaders()});return await this.handleResponse(e)}catch(e){throw e.name==="AbortError"?console.log("getActiveDownloads request aborted (timeout)"):console.error("getActiveDownloads error:",e),e}}static async getLatestDownloads(t,e=50){try{const a=await fetch(`${u}/downloads/latest?count=${e==="unlimited"?9999:e}`,{signal:t,headers:this.getHeaders()});return await this.handleResponse(a)}catch(r){throw r.name==="AbortError"?console.log("getLatestDownloads request aborted (timeout)"):console.error("getLatestDownloads error:",r),r}}static async getClientStats(t){try{const e=await fetch(`${u}/stats/clients`,{signal:t,headers:this.getHeaders()});return await this.handleResponse(e)}catch(e){throw e.name==="AbortError"?console.log("getClientStats request aborted (timeout)"):console.error("getClientStats error:",e),e}}static async getServiceStats(t,e=null){try{const r=e?`${u}/stats/services?since=${e}`:`${u}/stats/services`,a=await fetch(r,{signal:t,headers:this.getHeaders()});return await this.handleResponse(a)}catch(r){throw r.name==="AbortError"?console.log("getServiceStats request aborted (timeout)"):console.error("getServiceStats error:",r),r}}static async getDashboardStats(t="24h",e){try{const r=await fetch(`${u}/stats/dashboard?period=${t}`,{signal:e,headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw r.name==="AbortError"?console.log("getDashboardStats request aborted (timeout)"):console.error("getDashboardStats error:",r),r}}static async getCacheEffectiveness(t="24h",e){try{const r=await fetch(`${u}/stats/cache-effectiveness?period=${t}`,{signal:e,headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw r.name==="AbortError"?console.log("getCacheEffectiveness request aborted (timeout)"):console.error("getCacheEffectiveness error:",r),r}}static async getTimelineStats(t="24h",e="hourly",r){try{const a=await fetch(`${u}/stats/timeline?period=${t}&interval=${e}`,{signal:r,headers:this.getHeaders()});return await this.handleResponse(a)}catch(a){throw a.name==="AbortError"?console.log("getTimelineStats request aborted (timeout)"):console.error("getTimelineStats error:",a),a}}static async getBandwidthSaved(t="all",e){try{const r=await fetch(`${u}/stats/bandwidth-saved?period=${t}`,{signal:e,headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw r.name==="AbortError"?console.log("getBandwidthSaved request aborted (timeout)"):console.error("getBandwidthSaved error:",r),r}}static async getTopGames(t=10,e="7d",r){try{const a=await fetch(`${u}/stats/top-games?limit=${t}&period=${e}`,{signal:r,headers:this.getHeaders()});return await this.handleResponse(a)}catch(a){throw a.name==="AbortError"?console.log("getTopGames request aborted (timeout)"):console.error("getTopGames error:",a),a}}static async clearAllCache(){try{const t=await fetch(`${u}/management/cache/clear-all`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(1e4)});return await this.handleResponse(t)}catch(t){throw console.error("clearAllCache error:",t),t}}static async getCacheClearStatus(t){try{const e=await fetch(`${u}/management/cache/clear-status/${t}`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(e)}catch(e){throw console.error("getCacheClearStatus error:",e),e}}static async cancelCacheClear(t){try{const e=await fetch(`${u}/management/cache/clear-cancel/${t}`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(5e3)});return await this.handleResponse(e)}catch(e){throw console.error("cancelCacheClear error:",e),e}}static async getActiveCacheOperations(){try{const t=await fetch(`${u}/management/cache/active-operations`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(t)}catch(t){throw console.error("getActiveCacheOperations error:",t),t}}static async clearCache(t=null){return t?await this.removeServiceFromLogs(t):await this.clearAllCache()}static async resetDatabase(){try{const t=await fetch(`${u}/management/database`,{method:"DELETE",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(6e4)});return await this.handleResponse(t)}catch(t){throw console.error("resetDatabase error:",t),t}}static async resetLogPosition(){try{const t=await fetch(`${u}/management/reset-logs`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(6e4)});return await this.handleResponse(t)}catch(t){throw console.error("resetLogPosition error:",t),t}}static async processAllLogs(){try{const t=await fetch(`${u}/management/process-all-logs`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(12e4)});return await this.handleResponse(t)}catch(t){throw console.error("processAllLogs error:",t),t}}static async cancelProcessing(){try{const t=await fetch(`${u}/management/cancel-processing`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(1e4)});return await this.handleResponse(t)}catch(t){throw console.error("cancelProcessing error:",t),t}}static async getProcessingStatus(){try{const t=await fetch(`${u}/management/processing-status`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(t)}catch(t){throw console.error("getProcessingStatus error:",t),t}}static async removeServiceFromLogs(t){try{const e=await fetch(`${u}/management/logs/remove-service`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),body:JSON.stringify({service:t}),signal:AbortSignal.timeout(12e4)});return await this.handleResponse(e)}catch(e){throw console.error("removeServiceFromLogs error:",e),e}}static async getServiceLogCounts(){try{const t=await fetch(`${u}/management/logs/service-counts`,{signal:AbortSignal.timeout(3e4),headers:this.getHeaders()});return await this.handleResponse(t)}catch(t){throw console.error("getServiceLogCounts error:",t),t}}static async getConfig(){try{const t=await fetch(`${u}/management/config`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(t)}catch(t){return console.error("getConfig error:",t),{cachePath:"/cache",logPath:"/logs/access.log",services:["steam","epic","origin","blizzard","wsus","riot"]}}}}class Q{static generateMockData(t=50){const e=["192.168.1.100","192.168.1.101","192.168.1.102","192.168.1.103","192.168.1.104","192.168.1.105","192.168.1.106","192.168.1.107","192.168.1.108","192.168.1.109","192.168.1.110","192.168.1.111","10.0.0.50","10.0.0.51","10.0.0.52","10.0.0.53","127.0.0.1"],r=[{name:"Counter-Strike 2",size:32212254720},{name:"Dota 2",size:37580963840},{name:"Team Fortress 2",size:26843545600},{name:"Grand Theft Auto V",size:102005473280},{name:"Apex Legends",size:64424509440},{name:"Dead by Daylight",size:48318382080},{name:"Marvel Rivals",size:59055800320},{name:"Path of Exile",size:42949672960},{name:"Warframe",size:53687091200},{name:"Destiny 2",size:112742891520},{name:"Rust",size:37580963840},{name:"Valheim",size:1073741824},{name:"Unknown Steam Game",size:16106127360}],a={totalCacheSize:2e12,usedCacheSize:145e10,freeCacheSize:55e10,usagePercent:72.5,totalFiles:48293+(typeof t=="number"?t:500)*100,serviceSizes:{steam:65e10,epic:32e10,origin:18e10,blizzard:15e10,wsus:1e11,riot:5e10}},s=[],i=new Date,h=t==="unlimited"?500:t,n={};for(let c=0;c<h;c++){const l=N[Math.floor(Math.random()*N.length)],f=e[Math.floor(Math.random()*e.length)],w=Math.random()<.3,k=Math.pow(c/h,2)*2160,g=new Date(i.getTime()-k*60*60*1e3-Math.random()*36e5);let S;if(w){const D=new Date(g.getTime()+Math.random()*5e3);S={id:c+1,service:l,clientIp:f,startTime:g.toISOString(),endTime:D.toISOString(),cacheHitBytes:0,cacheMissBytes:0,totalBytes:0,cacheHitPercent:0,isActive:!1,gameName:null}}else{let D=null,E;if(l==="steam"&&Math.random()<.7){const H=r[Math.floor(Math.random()*r.length)];D=H.name,E=Math.floor(H.size*(.8+Math.random()*.2))}else E=Math.floor(Math.random()*50*1024*1024*1024);const T=Math.min(.95,.1+k/2160*.85),R=Math.floor(E*T),V=E-R,W=T>.8?500*1024*1024:50*1024*1024,F=E/W*1e3,X=new Date(g.getTime()+F);S={id:c+1,service:l,clientIp:f,startTime:g.toISOString(),endTime:X.toISOString(),cacheHitBytes:R,cacheMissBytes:V,totalBytes:E,cacheHitPercent:R/E*100,isActive:c<3&&k<.5,gameName:D,gameAppId:D&&D!=="Unknown Steam Game"?2e5+Math.floor(Math.random()*2e6):null}}n[f]||(n[f]={totalCacheHitBytes:0,totalCacheMissBytes:0,totalDownloads:0,lastSeen:g}),n[f].totalCacheHitBytes+=S.cacheHitBytes||0,n[f].totalCacheMissBytes+=S.cacheMissBytes||0,n[f].totalDownloads+=1,g>n[f].lastSeen&&(n[f].lastSeen=g),s.push(S)}s.sort((c,l)=>new Date(l.startTime).getTime()-new Date(c.startTime).getTime());const b=e.map(c=>{const l=n[c];if(l){const f=l.totalCacheHitBytes+l.totalCacheMissBytes;return{clientIp:c,totalCacheHitBytes:l.totalCacheHitBytes,totalCacheMissBytes:l.totalCacheMissBytes,totalBytes:f,cacheHitPercent:f>0?l.totalCacheHitBytes/f*100:0,totalDownloads:l.totalDownloads,lastSeen:l.lastSeen.toISOString()}}else return{clientIp:c,totalCacheHitBytes:0,totalCacheMissBytes:0,totalBytes:0,cacheHitPercent:0,totalDownloads:0,lastSeen:null}}).filter(c=>c.totalBytes>0),v=N.map(c=>{var k;const l=s.filter(g=>g.service===c),f=l.reduce((g,S)=>g+S.cacheHitBytes,0),w=l.reduce((g,S)=>g+S.cacheMissBytes,0);return{service:c,totalCacheHitBytes:f||a.serviceSizes[c]*.8,totalCacheMissBytes:w||a.serviceSizes[c]*.2,totalBytes:f+w||a.serviceSizes[c],cacheHitPercent:f+w>0?f/(f+w)*100:80,totalDownloads:l.length,lastActivity:((k=l[0])==null?void 0:k.startTime)||new Date(i.getTime()-Math.random()*72e5).toISOString()}});return{cacheInfo:a,activeDownloads:s.filter(c=>c.isActive),latestDownloads:s,clientStats:b,serviceStats:v}}static generateRealtimeUpdate(){const t=["192.168.1.100","192.168.1.101","192.168.1.102","192.168.1.103","192.168.1.104","192.168.1.105","192.168.1.106","192.168.1.107"];if(Math.random()<.2)return{id:Date.now(),service:N[Math.floor(Math.random()*N.length)],clientIp:t[Math.floor(Math.random()*t.length)],startTime:new Date().toISOString(),endTime:new Date().toISOString(),cacheHitBytes:0,cacheMissBytes:0,totalBytes:0,cacheHitPercent:0,isActive:!1};const r=Math.floor(Math.random()*5e8),a=Math.floor(Math.random()*1e8);return{id:Date.now(),service:N[Math.floor(Math.random()*N.length)],clientIp:t[Math.floor(Math.random()*t.length)],startTime:new Date().toISOString(),endTime:null,cacheHitBytes:r,cacheMissBytes:a,totalBytes:r+a,cacheHitPercent:r/(r+a)*100,isActive:!0,gameName:Math.random()<.5?"Counter-Strike 2":null}}}const ee={},oe=d.createContext(void 0),Ye=()=>{const m=d.useContext(oe);if(!m)throw new Error("useData must be used within DataProvider");return m},Ee=({children:m})=>{const[t,e]=d.useState(!1),[r,a]=d.useState(20),[s,i]=d.useState(20),[h,n]=d.useState(null),[b,v]=d.useState([]),[c,l]=d.useState([]),[f,w]=d.useState([]),[k,g]=d.useState([]),[S,D]=d.useState(!0),[E,T]=d.useState(null),[R,V]=d.useState(!1),[W,F]=d.useState(null),[X,H]=d.useState("checking"),O=d.useRef(!0),M=d.useRef(!1),Y=d.useRef(!1),p=d.useRef(null),C=d.useRef(null),se=()=>{if(!(typeof import.meta<"u"&&(ee!=null&&ee.VITE_API_URL)))return""},ne=async()=>{try{const x=se();return(await fetch(`${x}/health`,{signal:AbortSignal.timeout(5e3)})).ok?(H("connected"),!0):(H("error"),!1)}catch{return H("disconnected"),!1}},U=async()=>{if(!(Y.current&&!O.current)){Y.current=!0,p.current&&p.current.abort(),p.current=new AbortController;try{if(O.current&&D(!0),t){const x=r==="unlimited"?100:Math.min(Number(r),100),y=Q.generateMockData(x);n(y.cacheInfo),v(y.activeDownloads),l(y.latestDownloads),w(y.clientStats),g(y.serviceStats),T(null),H("connected"),M.current=!0}else if(await ne())try{const K=setTimeout(()=>{var B;return(B=p.current)==null?void 0:B.abort()},R?3e4:1e4);if(O.current){const[B,A]=await Promise.all([I.getCacheInfo(p.current.signal),I.getActiveDownloads(p.current.signal)]);B&&n(B),A&&v(A);const j=await I.getLatestDownloads(p.current.signal,20);j&&(l(j),M.current=!0),setTimeout(async()=>{var $;if(!(($=p.current)!=null&&$.signal.aborted))try{const[P,L]=await Promise.all([I.getClientStats(p.current.signal),I.getServiceStats(p.current.signal)]);P&&w(P),L&&g(L)}catch(P){console.log("Deferred stats fetch error:",P)}},100)}else{const B=s==="unlimited"?100:s,[A,j,$,P,L]=await Promise.allSettled([I.getCacheInfo(p.current.signal),I.getActiveDownloads(p.current.signal),I.getLatestDownloads(p.current.signal,B),I.getClientStats(p.current.signal),I.getServiceStats(p.current.signal)]);A.status==="fulfilled"&&A.value!==void 0&&n(A.value),j.status==="fulfilled"&&j.value!==void 0&&v(j.value),$.status==="fulfilled"&&$.value!==void 0&&(l($.value),M.current=!0,R&&$.value.length>0&&F(he=>({...he,message:`Processing logs... Found ${$.value.length} downloads`,downloadCount:$.value.length}))),P.status==="fulfilled"&&P.value!==void 0&&w(P.value),L.status==="fulfilled"&&L.value!==void 0&&g(L.value)}clearTimeout(K),T(null)}catch(y){M.current||(y.name==="AbortError"?T("Request timeout - the server may be busy"):T("Failed to fetch data from API"))}else M.current||T("Cannot connect to API server")}catch(x){console.error("Error in fetchData:",x),!M.current&&!t&&T("An unexpected error occurred")}finally{O.current&&(D(!1),O.current=!1),Y.current=!1}}},ce=x=>{if(t){const y=x==="unlimited"?100:Math.min(x,100);a(y)}},ie=x=>{i(x==="unlimited"?100:x)},te=()=>R?15e3:s==="unlimited"||s>100?3e4:Ae;d.useEffect(()=>{if(!t){U();const x=te();return C.current=setInterval(U,x),()=>{C.current&&(clearInterval(C.current),C.current=null),p.current&&p.current.abort()}}},[R,t,s]),d.useEffect(()=>{if(t){C.current&&(clearInterval(C.current),C.current=null),n(null),v([]),l([]),w([]),g([]);const x=r==="unlimited"?100:Math.min(Number(r),100),y=Q.generateMockData(x);n(y.cacheInfo),v(y.activeDownloads),l(y.latestDownloads),w(y.clientStats),g(y.serviceStats),T(null),H("connected"),M.current=!0;const K=3e4;return C.current=setInterval(()=>{const B=Q.generateRealtimeUpdate();l(A=>[B,...A.slice(0,99)]),v(A=>[B,...A.filter($=>$.id!==B.id)].slice(0,5))},K),()=>{C.current&&(clearInterval(C.current),C.current=null)}}else n(null),v([]),l([]),w([]),g([]),T(null),M.current=!1,O.current=!0,U()},[t,r]),d.useEffect(()=>()=>{C.current&&clearInterval(C.current),p.current&&p.current.abort()},[]);const le={mockMode:t,setMockMode:e,mockDownloadCount:r,updateMockDataCount:ce,apiDownloadCount:s,updateApiDownloadCount:ie,cacheInfo:h,activeDownloads:b,latestDownloads:c,clientStats:f,serviceStats:k,loading:S,error:E,fetchData:U,clearAllData:()=>{n(null),v([]),l([]),w([]),g([]),M.current=!1},isProcessingLogs:R,setIsProcessingLogs:V,processingStatus:W,setProcessingStatus:F,connectionStatus:X,getCurrentRefreshInterval:te};return o.jsx(oe.Provider,{value:le,children:m})},Re=({title:m="LANCache Manager",subtitle:t="High-performance cache monitoring & management",connectionStatus:e="connected"})=>{const a=(()=>{switch(e){case"connected":return{color:"cache-hit",text:"Connected",icon:o.jsx(q,{className:"w-4 h-4"})};case"disconnected":return{color:"text-themed-error",text:"Disconnected",icon:o.jsx(q,{className:"w-4 h-4"})};case"reconnecting":return{color:"cache-miss",text:"Reconnecting...",icon:o.jsx(q,{className:"w-4 h-4 animate-pulse"})};default:return{color:"text-themed-muted",text:"Unknown",icon:o.jsx(q,{className:"w-4 h-4"})}}})();return o.jsx("header",{className:"border-b",style:{backgroundColor:"var(--theme-nav-bg)",borderColor:"var(--theme-nav-border)"},children:o.jsx("div",{className:"container mx-auto px-4",children:o.jsxs("div",{className:"flex items-center justify-between h-16 min-w-0",children:[o.jsxs("div",{className:"flex items-center space-x-2 sm:space-x-3 min-w-0 flex-1",children:[o.jsx("div",{className:"p-1.5 sm:p-2 rounded-lg flex-shrink-0",style:{backgroundColor:"var(--theme-icon-blue)"},children:o.jsx(ue,{className:"w-5 h-5 sm:w-6 sm:h-6 text-white"})}),o.jsxs("div",{className:"min-w-0 flex-1",children:[o.jsx("h1",{className:"text-lg sm:text-xl font-bold text-themed-primary truncate",children:m}),o.jsx("p",{className:"text-xs sm:text-sm text-themed-muted truncate hidden sm:block",children:t})]})]}),o.jsx("div",{className:"flex items-center space-x-1 sm:space-x-2 flex-shrink-0",children:o.jsxs("div",{className:`flex items-center space-x-1 ${a.color}`,children:[a.icon,o.jsx("span",{className:"text-xs sm:text-sm font-medium hidden sm:inline",children:a.text})]})})]})})})},Me=({activeTab:m,setActiveTab:t})=>{var i;const[e,r]=d.useState(!1),a=[{id:"dashboard",label:"Dashboard",icon:fe},{id:"downloads",label:"Downloads",icon:ge},{id:"clients",label:"Clients",icon:be},{id:"management",label:"Management",icon:pe}],s=({tab:h,isActive:n,onClick:b,className:v=""})=>{const c=h.icon;return o.jsxs("button",{onClick:b,className:`flex items-center space-x-2 px-3 py-2 rounded-lg font-medium transition-all duration-200 ${v}`,style:{color:n?"var(--theme-nav-tab-active)":"var(--theme-nav-tab-inactive)",backgroundColor:"transparent"},onMouseEnter:l=>{n||(l.currentTarget.style.color="var(--theme-nav-tab-hover)",l.currentTarget.style.backgroundColor="var(--theme-nav-mobile-item-hover)")},onMouseLeave:l=>{n||(l.currentTarget.style.color="var(--theme-nav-tab-inactive)",l.currentTarget.style.backgroundColor="transparent")},children:[o.jsx(c,{className:"w-5 h-5"}),o.jsx("span",{children:h.label}),n&&o.jsx("div",{className:"absolute bottom-0 left-0 right-0 h-0.5 rounded-full",style:{backgroundColor:"var(--theme-nav-tab-active-border)"}})]})};return o.jsx("nav",{className:"border-b",style:{backgroundColor:"var(--theme-nav-bg)",borderColor:"var(--theme-nav-border)"},children:o.jsxs("div",{className:"container mx-auto px-4",children:[o.jsx("div",{className:"hidden md:flex space-x-1 h-12 items-center",children:a.map(h=>o.jsx("div",{className:"relative",children:o.jsx(s,{tab:h,isActive:m===h.id,onClick:()=>t(h.id)})},h.id))}),o.jsxs("div",{className:"md:hidden",children:[o.jsxs("div",{className:"flex items-center justify-between h-12",children:[o.jsx("div",{className:"flex items-center space-x-2",children:o.jsx("span",{className:"font-medium text-themed-primary",children:((i=a.find(h=>h.id===m))==null?void 0:i.label)||"Dashboard"})}),o.jsx("button",{onClick:()=>r(!e),className:"p-2 rounded-lg transition-colors",style:{color:"var(--theme-nav-tab-inactive)",backgroundColor:"transparent"},onMouseEnter:h=>{h.currentTarget.style.backgroundColor="var(--theme-nav-mobile-item-hover)"},onMouseLeave:h=>{h.currentTarget.style.backgroundColor="transparent"},children:e?o.jsx(ve,{className:"w-5 h-5"}):o.jsx(ye,{className:"w-5 h-5"})})]}),e&&o.jsx("div",{className:"border-t py-2 space-y-1",style:{backgroundColor:"var(--theme-nav-mobile-menu-bg)",borderColor:"var(--theme-nav-border)"},children:a.map(h=>o.jsx(s,{tab:h,isActive:m===h.id,onClick:()=>{t(h.id),r(!1)},className:"w-full justify-start"},h.id))})]})]})})};class ke extends d.Component{constructor(t){super(t),this.state={hasError:!1}}static getDerivedStateFromError(t){return{hasError:!0,error:t}}componentDidCatch(t,e){console.error("Error caught by boundary:",t,e)}render(){var t;return this.state.hasError?o.jsx("div",{className:"min-h-screen bg-themed-primary flex items-center justify-center p-4",children:o.jsx("div",{className:"alert-error rounded-lg p-6 max-w-md w-full",children:o.jsxs("div",{className:"flex items-start space-x-3",children:[o.jsx(xe,{className:"w-5 h-5 mt-0.5"}),o.jsxs("div",{children:[o.jsx("h3",{className:"text-lg font-semibold mb-2",children:"Something went wrong"}),o.jsx("p",{className:"text-sm text-themed-secondary",children:((t=this.state.error)==null?void 0:t.message)||"An unexpected error occurred"}),o.jsx("button",{onClick:()=>window.location.reload(),className:"mt-4 px-4 py-2 action-delete rounded-lg text-sm smooth-transition",children:"Reload Page"})]})]})})}):this.props.children}}const He=({message:m,size:t="md",fullScreen:e=!1})=>{const r={xs:"w-4 h-4",sm:"w-6 h-6",md:"w-8 h-8",lg:"w-12 h-12",xl:"w-16 h-16"},a=o.jsxs("div",{className:"flex flex-col items-center justify-center space-y-4",children:[o.jsx(we,{className:`${r[t]} text-themed-primary animate-spin`}),m&&o.jsx("p",{className:"text-sm text-themed-muted",children:m})]});return e?o.jsx("div",{className:"fixed inset-0 bg-themed-primary bg-opacity-50 flex items-center justify-center z-50",children:a}):o.jsx("div",{className:"flex items-center justify-center min-h-[200px]",children:a})},ae=d.lazy(()=>z(()=>import("./Dashboard-B7wxeKTI.js"),__vite__mapDeps([0,1,2,3,4,5,6]))),je=d.lazy(()=>z(()=>import("./DownloadsTab-D9kbXAvS.js"),__vite__mapDeps([7,1,2,3,4,8]))),Pe=d.lazy(()=>z(()=>import("./ClientsTab-eDHaghpV.js"),__vite__mapDeps([9,1,2,3,4,6]))),_e=d.lazy(()=>z(()=>import("./ServicesTab-C2Ksvi4a.js"),__vite__mapDeps([10,1,2,3,4,6]))),Ne=d.lazy(()=>z(()=>import("./ManagementTab-BX7Gk_CV.js"),__vite__mapDeps([11,1,2,3,4,8,12]))),Oe=()=>{const[m,t]=d.useState("dashboard"),e=()=>{const r=(()=>{switch(m){case"dashboard":return ae;case"downloads":return je;case"clients":return Pe;case"services":return _e;case"management":return Ne;default:return ae}})();return o.jsx(d.Suspense,{fallback:o.jsx(He,{fullScreen:!1,message:"Loading..."}),children:o.jsx(r,{})})};return o.jsx(ke,{children:o.jsx(Ee,{children:o.jsxs("div",{className:"min-h-screen",style:{backgroundColor:"var(--theme-bg-primary)",color:"var(--theme-text-primary)"},children:[o.jsx(Re,{}),o.jsx(Me,{activeTab:m,setActiveTab:t}),o.jsx("main",{className:"container mx-auto px-4 py-6",children:e()})]})})})};class Le{constructor(){_(this,"currentTheme",null);_(this,"themes",[]);_(this,"styleElement",null)}async loadThemes(){const t=this.getBuiltInThemes(),e=[],r=[];try{const i=await fetch(`${u}/theme`);if(i.ok){const h=await i.json();for(const n of h)if(n.format==="toml")try{const b=await fetch(`${u}/theme/${n.id}`);if(b.status===404){r.push(n.id),console.log(`Theme ${n.id} no longer exists on server`);continue}if(b.ok){const v=await b.text(),c=this.parseTomlTheme(v);c&&e.push(c)}}catch(b){console.error(`Failed to load theme ${n.id}:`,b)}if(r.length>0){const n=localStorage.getItem("lancache_theme");if(n&&r.includes(n)){console.log(`Current theme ${n} was deleted, resetting to default`),localStorage.removeItem("lancache_theme"),localStorage.removeItem("lancache_theme_applied");const b=t.find(v=>v.meta.id==="dark-default");b&&this.applyTheme(b)}}}}catch(i){console.error("Failed to load themes from server:",i)}const a=[...t],s=new Set(a.map(i=>i.meta.id));return e.forEach(i=>{s.has(i.meta.id)||(a.push(i),s.add(i.meta.id))}),this.themes=a,this.themes}getBuiltInThemes(){return[{meta:{id:"dark-default",name:"Dark Default",description:"Default dark theme with blue accents",author:"System",version:"1.0.0",isDark:!0},colors:{primaryColor:"#3b82f6",secondaryColor:"#8b5cf6",accentColor:"#06b6d4",bgPrimary:"#111827",bgSecondary:"#1f2937",bgTertiary:"#374151",bgHover:"#4b5563",textPrimary:"#ffffff",textSecondary:"#d1d5db",textMuted:"#9ca3af",textAccent:"#60a5fa",borderPrimary:"#374151",borderSecondary:"#4b5563",borderFocus:"#3b82f6",navBg:"#1f2937",navBorder:"#374151",navTabActive:"#3b82f6",navTabInactive:"#9ca3af",navTabHover:"#ffffff",navTabActiveBorder:"#3b82f6",navMobileMenuBg:"#1f2937",navMobileItemHover:"#374151",success:"#10b981",successBg:"#064e3b",successText:"#34d399",warning:"#fb923c",warningBg:"#44403c",warningText:"#fcd34d",error:"#ef4444",errorBg:"#7f1d1d",errorText:"#fca5a5",info:"#3b82f6",infoBg:"#1e3a8a",infoText:"#93c5fd",steamColor:"#3b82f6",epicColor:"#8b5cf6",originColor:"#10b981",blizzardColor:"#ef4444",wsusColor:"#06b6d4",riotColor:"#f59e0b",cardBg:"#1f2937",cardBorder:"#374151",buttonBg:"#3b82f6",buttonHover:"#2563eb",buttonText:"#ffffff",inputBg:"#374151",inputBorder:"#4b5563",inputFocus:"#3b82f6",badgeBg:"#3b82f6",badgeText:"#ffffff",progressBar:"#3b82f6",progressBg:"#374151",hitRateHighBg:"#064e3b",hitRateHighText:"#34d399",hitRateMediumBg:"#1e3a8a",hitRateMediumText:"#93c5fd",hitRateLowBg:"#44403c",hitRateLowText:"#fbbf24",hitRateWarningBg:"#44403c",hitRateWarningText:"#fcd34d",actionResetBg:"#f59e0b",actionResetHover:"#d97706",actionProcessBg:"#10b981",actionProcessHover:"#059669",actionDeleteBg:"#ef4444",actionDeleteHover:"#dc2626",iconBgBlue:"#3b82f6",iconBgGreen:"#10b981",iconBgEmerald:"#10b981",iconBgPurple:"#8b5cf6",iconBgIndigo:"#6366f1",iconBgOrange:"#f97316",iconBgYellow:"#eab308",iconBgCyan:"#06b6d4",iconBgRed:"#ef4444",chartColor1:"#3b82f6",chartColor2:"#10b981",chartColor3:"#f59e0b",chartColor4:"#ef4444",chartColor5:"#8b5cf6",chartColor6:"#06b6d4",chartColor7:"#f97316",chartColor8:"#ec4899",chartBorderColor:"#1f2937",chartGridColor:"#374151",chartTextColor:"#9ca3af",chartCacheHitColor:"#10b981",chartCacheMissColor:"#f59e0b",scrollbarTrack:"#374151",scrollbarThumb:"#6B7280",scrollbarHover:"#9CA3AF"}},{meta:{id:"light-default",name:"Light Default",description:"Default light theme with blue accents",author:"System",version:"1.0.0",isDark:!1},colors:{primaryColor:"#3b82f6",secondaryColor:"#8b5cf6",accentColor:"#06b6d4",bgPrimary:"#f8f9fa",bgSecondary:"#ffffff",bgTertiary:"#f3f4f6",bgHover:"#e5e7eb",textPrimary:"#111827",textSecondary:"#374151",textMuted:"#6b7280",textAccent:"#2563eb",borderPrimary:"#e5e7eb",borderSecondary:"#d1d5db",borderFocus:"#3b82f6",navBg:"#ffffff",navBorder:"#e5e7eb",navTabActive:"#3b82f6",navTabInactive:"#6b7280",navTabHover:"#111827",navTabActiveBorder:"#3b82f6",navMobileMenuBg:"#ffffff",navMobileItemHover:"#f3f4f6",success:"#10b981",successBg:"#d1fae5",successText:"#047857",warning:"#f97316",warningBg:"#fef3c7",warningText:"#b45309",error:"#ef4444",errorBg:"#fee2e2",errorText:"#991b1b",info:"#3b82f6",infoBg:"#dbeafe",infoText:"#1e40af",steamColor:"#3b82f6",epicColor:"#8b5cf6",originColor:"#10b981",blizzardColor:"#ef4444",wsusColor:"#06b6d4",riotColor:"#f59e0b",cardBg:"#ffffff",cardBorder:"#e5e7eb",buttonBg:"#3b82f6",buttonHover:"#2563eb",buttonText:"#ffffff",inputBg:"#ffffff",inputBorder:"#d1d5db",inputFocus:"#3b82f6",badgeBg:"#3b82f6",badgeText:"#ffffff",progressBar:"#3b82f6",progressBg:"#e5e7eb",hitRateHighBg:"#d1fae5",hitRateHighText:"#047857",hitRateMediumBg:"#dbeafe",hitRateMediumText:"#1e40af",hitRateLowBg:"#fef3c7",hitRateLowText:"#92400e",hitRateWarningBg:"#fef3c7",hitRateWarningText:"#92400e",actionResetBg:"#f59e0b",actionResetHover:"#d97706",actionProcessBg:"#10b981",actionProcessHover:"#059669",actionDeleteBg:"#ef4444",actionDeleteHover:"#dc2626",iconBgBlue:"#3b82f6",iconBgGreen:"#10b981",iconBgEmerald:"#10b981",iconBgPurple:"#8b5cf6",iconBgIndigo:"#6366f1",iconBgOrange:"#f97316",iconBgYellow:"#eab308",iconBgCyan:"#06b6d4",iconBgRed:"#ef4444",chartColor1:"#3b82f6",chartColor2:"#10b981",chartColor3:"#f59e0b",chartColor4:"#ef4444",chartColor5:"#8b5cf6",chartColor6:"#06b6d4",chartColor7:"#f97316",chartColor8:"#ec4899",chartBorderColor:"#e5e7eb",chartGridColor:"#d1d5db",chartTextColor:"#6b7280",chartCacheHitColor:"#047857",chartCacheMissColor:"#b45309",scrollbarTrack:"#e5e7eb",scrollbarThumb:"#9ca3af",scrollbarHover:"#6b7280"}}]}async getTheme(t){const e=this.getBuiltInThemes().find(a=>a.meta.id===t);if(e)return e;const r=this.themes.find(a=>a.meta.id===t);if(r)return r;try{const a=await fetch(`${u}/theme/${t}`);if(!a.ok)return null;const s=await a.text();return this.parseTomlTheme(s)}catch(a){return console.error("Error loading theme:",a),null}}parseTomlTheme(t){try{const e=Te.parse(t);return!e.meta||!e.meta.id||!e.meta.name?(console.error("Invalid theme: missing meta.id or meta.name"),null):e.colors?e:(console.error("Invalid theme: missing colors section"),null)}catch(e){return console.error("Error parsing TOML theme:",e),null}}async uploadTheme(t){const e=await t.text(),r=this.parseTomlTheme(e);if(!r)throw new Error("Invalid TOML theme format");const a=new FormData;a.append("file",t);const s=await fetch(`${u}/theme/upload`,{method:"POST",headers:G.getAuthHeaders(),body:a});if(!s.ok){const i=await s.json().catch(()=>({error:"Failed to upload theme"}));throw new Error(i.error||"Failed to upload theme")}return await this.loadThemes(),r}async deleteTheme(t){const e=await fetch(`${u}/theme/${t}`,{method:"DELETE",headers:G.getAuthHeaders()});if(!e.ok&&e.status!==404){const r=await e.json().catch(()=>({error:"Failed to delete theme"}));throw new Error(r.error||"Failed to delete theme")}await this.loadThemes()}applyDefaultVariables(){const t=`
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
        --theme-border-primary: #374151;
        --theme-border-secondary: #4b5563;
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
    `;let e=document.getElementById("lancache-default-vars");e||(e=document.createElement("style"),e.id="lancache-default-vars",document.head.appendChild(e)),e.textContent=t}clearTheme(){this.styleElement&&(this.styleElement.remove(),this.styleElement=null);const t=document.documentElement;t.removeAttribute("data-theme"),t.removeAttribute("data-theme-id"),localStorage.removeItem("lancache_theme"),localStorage.removeItem("lancache_theme_applied"),this.currentTheme=null,this.applyDefaultVariables()}applyTheme(t){var s;if(!t||!t.colors)return;this.styleElement&&(this.styleElement.remove(),this.styleElement=null);const e=t.colors,r=`
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
      --theme-border-primary: ${e.borderPrimary||"#374151"};
      --theme-border-secondary: ${e.borderSecondary||"#4b5563"};
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
    ${((s=t.css)==null?void 0:s.content)||""}
  `;this.styleElement=document.createElement("style"),this.styleElement.id="lancache-theme",this.styleElement.textContent=r,document.head.appendChild(this.styleElement);const a=document.documentElement;a.setAttribute("data-theme",t.meta.isDark?"dark":"light"),a.setAttribute("data-theme-id",t.meta.id),localStorage.setItem("lancache_theme",t.meta.id),localStorage.setItem("lancache_theme_applied","true"),this.currentTheme=t,window.dispatchEvent(new Event("themechange"))}async loadSavedTheme(){this.applyDefaultVariables(),await this.loadThemes();const t=localStorage.getItem("lancache_theme_applied")==="true",e=localStorage.getItem("lancache_theme");if(t&&e){const r=await this.getTheme(e);if(r)this.applyTheme(r);else{console.log(`Saved theme ${e} not found, resetting to default`),localStorage.removeItem("lancache_theme"),localStorage.removeItem("lancache_theme_applied");const a=await this.getTheme("dark-default");a&&this.applyTheme(a)}}else if(!t){const r=await this.getTheme("dark-default");r&&this.applyTheme(r)}}getCurrentThemeId(){return localStorage.getItem("lancache_theme_applied")==="true"&&localStorage.getItem("lancache_theme")||"dark-default"}getCurrentTheme(){return localStorage.getItem("lancache_theme_applied")==="true"?this.currentTheme:this.getBuiltInThemes().find(e=>e.meta.id==="dark-default")||null}isThemeApplied(){return localStorage.getItem("lancache_theme_applied")==="true"}exportTheme(t){var r;let e="";return e+=`[meta]
`,e+=`name = "${t.meta.name}"
`,e+=`id = "${t.meta.id}"
`,t.meta.description&&(e+=`description = "${t.meta.description}"
`),t.meta.author&&(e+=`author = "${t.meta.author}"
`),t.meta.version&&(e+=`version = "${t.meta.version}"
`),t.meta.isDark!==void 0&&(e+=`isDark = ${t.meta.isDark}
`),e+=`
`,e+=`[colors]
`,t.colors&&Object.entries(t.colors).forEach(([a,s])=>{e+=`${a} = "${s}"
`}),e+=`
`,t.custom&&Object.keys(t.custom).length>0&&(e+=`[custom]
`,Object.entries(t.custom).forEach(([a,s])=>{e+=`"${a}" = "${s}"
`}),e+=`
`),(r=t.css)!=null&&r.content&&(e+=`[css]
`,e+=`content = """
${t.css.content}
"""
`),e}}const ze=new Le;ze.loadSavedTheme();Ce.createRoot(document.getElementById("root")).render(o.jsx(Se.StrictMode,{children:o.jsx(Oe,{})}));export{I as A,We as F,Xe as S,G as a,u as b,ze as t,Ye as u};
//# sourceMappingURL=index-B2aET8M8.js.map
