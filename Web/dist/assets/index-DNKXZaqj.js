const __vite__mapDeps=(i,m=__vite__mapDeps,d=(m.f||(m.f=["assets/Dashboard-B2-YEbMz.js","assets/react-vendor-CesVvHrP.js","assets/vendor-BmYUbuLR.js","assets/tanstack-CPlEqbpz.js","assets/Card-2XTxyUKx.js","assets/charts-B1PwCyAI.js","assets/Tooltip-CkSIWjPL.js","assets/DownloadsTab-Cu2PMXAj.js","assets/Alert-CK8L5O9K.js","assets/ClientsTab-BP8BuIRS.js","assets/ServicesTab-CDsKH870.js","assets/ManagementTab-DqorkMH0.js","assets/signalr-C7Jyn4vH.js"])))=>i.map(i=>d[i]);
var de=Object.defineProperty;var me=(m,e,r)=>e in m?de(m,e,{enumerable:!0,configurable:!0,writable:!0,value:r}):m[e]=r;var L=(m,e,r)=>me(m,typeof e!="symbol"?e+"":e,r);import{r as d,j as o,M as ue,W as q,L as fe,D as ge,U as be,S as ve,X as pe,a as ye,C as xe,b as we,c as Ce,R as Se}from"./react-vendor-CesVvHrP.js";import{t as Te}from"./vendor-BmYUbuLR.js";import"./tanstack-CPlEqbpz.js";(function(){const e=document.createElement("link").relList;if(e&&e.supports&&e.supports("modulepreload"))return;for(const t of document.querySelectorAll('link[rel="modulepreload"]'))a(t);new MutationObserver(t=>{for(const s of t)if(s.type==="childList")for(const n of s.addedNodes)n.tagName==="LINK"&&n.rel==="modulepreload"&&a(n)}).observe(document,{childList:!0,subtree:!0});function r(t){const s={};return t.integrity&&(s.integrity=t.integrity),t.referrerPolicy&&(s.referrerPolicy=t.referrerPolicy),t.crossOrigin==="use-credentials"?s.credentials="include":t.crossOrigin==="anonymous"?s.credentials="omit":s.credentials="same-origin",s}function a(t){if(t.ep)return;t.ep=!0;const s=r(t);fetch(t.href,s)}})();const Be="modulepreload",$e=function(m){return"/"+m},re={},z=function(e,r,a){let t=Promise.resolve();if(r&&r.length>0){document.getElementsByTagName("link");const n=document.querySelector("meta[property=csp-nonce]"),i=(n==null?void 0:n.nonce)||(n==null?void 0:n.getAttribute("nonce"));t=Promise.allSettled(r.map(c=>{if(c=$e(c),c in re)return;re[c]=!0;const v=c.endsWith(".css"),x=v?'[rel="stylesheet"]':"";if(document.querySelector(`link[href="${c}"]${x}`))return;const l=document.createElement("link");if(l.rel=v?"stylesheet":Be,v||(l.as="script"),l.crossOrigin="",l.href=c,i&&l.setAttribute("nonce",i),document.head.appendChild(l),v)return new Promise((h,f)=>{l.addEventListener("load",h),l.addEventListener("error",()=>f(new Error(`Unable to preload CSS for ${c}`)))})}))}function s(n){const i=new Event("vite:preloadError",{cancelable:!0});if(i.payload=n,window.dispatchEvent(i),!i.defaultPrevented)throw n}return t.then(n=>{for(const i of n||[])i.status==="rejected"&&s(i.reason);return e().catch(s)})},u="/api",N=["steam","epic","origin","blizzard","wsus","riot"],Ae=5e3,We=["B","KB","MB","GB","TB","PB"],Xe={DASHBOARD_CARD_ORDER:"lancache_dashboard_card_order",DASHBOARD_CARD_VISIBILITY:"lancache_dashboard_card_visibility"},J={},De=()=>{if(!(typeof import.meta<"u"&&(J!=null&&J.VITE_API_URL)))return""},Z=De();class Ie{constructor(){L(this,"deviceId");L(this,"isAuthenticated");L(this,"authChecked");this.deviceId=this.getOrCreateDeviceId(),this.isAuthenticated=!1,this.authChecked=!1}getOrCreateDeviceId(){let e=localStorage.getItem("lancache_device_id");return e||(e="xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g,function(r){const a=Math.random()*16|0;return(r==="x"?a:a&3|8).toString(16)}),localStorage.setItem("lancache_device_id",e)),e}async checkAuth(){try{const e=await fetch(`${Z}/api/auth/check`,{headers:{"X-Device-Id":this.deviceId}});if(e.ok){const r=await e.json();return this.isAuthenticated=r.isAuthenticated,this.authChecked=!0,r}return this.isAuthenticated=!1,this.authChecked=!0,{requiresAuth:!0,isAuthenticated:!1}}catch(e){return console.error("Auth check failed:",e),this.authChecked=!0,{requiresAuth:!1,isAuthenticated:!1,error:e.message}}}async register(e,r=null){try{const a=await fetch(`${Z}/api/auth/register`,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({deviceId:this.deviceId,apiKey:e,deviceName:r||this.getDeviceName()})}),t=await a.json();return a.ok&&t.success?(this.isAuthenticated=!0,localStorage.setItem("lancache_auth_registered","true"),{success:!0,message:t.message}):{success:!1,message:t.message||"Registration failed"}}catch(a){return console.error("Registration failed:",a),{success:!1,message:a.message||"Network error during registration"}}}async regenerateApiKey(){try{const e=await fetch(`${Z}/api/auth/regenerate-key`,{method:"POST",headers:{"Content-Type":"application/json","X-Device-Id":this.deviceId}}),r=await e.json();return e.ok&&r.success?(this.clearAuth(),this.isAuthenticated=!1,{success:!0,message:r.message,warning:r.warning}):{success:!1,message:r.message||"Failed to regenerate API key"}}catch(e){return console.error("Failed to regenerate API key:",e),{success:!1,message:e.message||"Network error while regenerating API key"}}}getDeviceName(){const e=navigator.userAgent;let r="Unknown OS",a="Unknown Browser";return e.indexOf("Win")!==-1?r="Windows":e.indexOf("Mac")!==-1?r="macOS":e.indexOf("Linux")!==-1?r="Linux":e.indexOf("Android")!==-1?r="Android":e.indexOf("iOS")!==-1&&(r="iOS"),e.indexOf("Chrome")!==-1?a="Chrome":e.indexOf("Safari")!==-1?a="Safari":e.indexOf("Firefox")!==-1?a="Firefox":e.indexOf("Edge")!==-1&&(a="Edge"),`${a} on ${r}`}getAuthHeaders(){return{"X-Device-Id":this.deviceId}}handleUnauthorized(){this.isAuthenticated=!1,localStorage.removeItem("lancache_auth_registered")}clearAuth(){this.isAuthenticated=!1,localStorage.removeItem("lancache_auth_registered")}isRegistered(){return localStorage.getItem("lancache_auth_registered")==="true"}}const G=new Ie;class D{static async handleResponse(e){if(e.status===401){G.handleUnauthorized();const r=await e.text().catch(()=>"");throw new Error(`Authentication required: ${r||"Please provide API key"}`)}if(!e.ok){const r=await e.text().catch(()=>"");throw new Error(`HTTP ${e.status}: ${r||e.statusText}`)}return e.json()}static getHeaders(e={}){return{...G.getAuthHeaders(),...e}}static async getCacheInfo(e){try{const r=await fetch(`${u}/management/cache`,{signal:e,headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw r.name==="AbortError"?console.log("getCacheInfo request aborted (timeout)"):console.error("getCacheInfo error:",r),r}}static async getActiveDownloads(e){try{const r=await fetch(`${u}/downloads/active`,{signal:e,headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw r.name==="AbortError"?console.log("getActiveDownloads request aborted (timeout)"):console.error("getActiveDownloads error:",r),r}}static async getLatestDownloads(e,r=50){try{const t=await fetch(`${u}/downloads/latest?count=${r==="unlimited"?9999:r}`,{signal:e,headers:this.getHeaders()});return await this.handleResponse(t)}catch(a){throw a.name==="AbortError"?console.log("getLatestDownloads request aborted (timeout)"):console.error("getLatestDownloads error:",a),a}}static async getClientStats(e){try{const r=await fetch(`${u}/stats/clients`,{signal:e,headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw r.name==="AbortError"?console.log("getClientStats request aborted (timeout)"):console.error("getClientStats error:",r),r}}static async getServiceStats(e,r=null){try{const a=r?`${u}/stats/services?since=${r}`:`${u}/stats/services`,t=await fetch(a,{signal:e,headers:this.getHeaders()});return await this.handleResponse(t)}catch(a){throw a.name==="AbortError"?console.log("getServiceStats request aborted (timeout)"):console.error("getServiceStats error:",a),a}}static async getDashboardStats(e="24h",r){try{const a=await fetch(`${u}/stats/dashboard?period=${e}`,{signal:r,headers:this.getHeaders()});return await this.handleResponse(a)}catch(a){throw a.name==="AbortError"?console.log("getDashboardStats request aborted (timeout)"):console.error("getDashboardStats error:",a),a}}static async getCacheEffectiveness(e="24h",r){try{const a=await fetch(`${u}/stats/cache-effectiveness?period=${e}`,{signal:r,headers:this.getHeaders()});return await this.handleResponse(a)}catch(a){throw a.name==="AbortError"?console.log("getCacheEffectiveness request aborted (timeout)"):console.error("getCacheEffectiveness error:",a),a}}static async getTimelineStats(e="24h",r="hourly",a){try{const t=await fetch(`${u}/stats/timeline?period=${e}&interval=${r}`,{signal:a,headers:this.getHeaders()});return await this.handleResponse(t)}catch(t){throw t.name==="AbortError"?console.log("getTimelineStats request aborted (timeout)"):console.error("getTimelineStats error:",t),t}}static async getBandwidthSaved(e="all",r){try{const a=await fetch(`${u}/stats/bandwidth-saved?period=${e}`,{signal:r,headers:this.getHeaders()});return await this.handleResponse(a)}catch(a){throw a.name==="AbortError"?console.log("getBandwidthSaved request aborted (timeout)"):console.error("getBandwidthSaved error:",a),a}}static async getTopGames(e=10,r="7d",a){try{const t=await fetch(`${u}/stats/top-games?limit=${e}&period=${r}`,{signal:a,headers:this.getHeaders()});return await this.handleResponse(t)}catch(t){throw t.name==="AbortError"?console.log("getTopGames request aborted (timeout)"):console.error("getTopGames error:",t),t}}static async clearAllCache(){try{const e=await fetch(`${u}/management/cache/clear-all`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(1e4)});return await this.handleResponse(e)}catch(e){throw console.error("clearAllCache error:",e),e}}static async getCacheClearStatus(e){try{const r=await fetch(`${u}/management/cache/clear-status/${e}`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(r)}catch(r){throw console.error("getCacheClearStatus error:",r),r}}static async cancelCacheClear(e){try{const r=await fetch(`${u}/management/cache/clear-cancel/${e}`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(5e3)});return await this.handleResponse(r)}catch(r){throw console.error("cancelCacheClear error:",r),r}}static async getActiveCacheOperations(){try{const e=await fetch(`${u}/management/cache/active-operations`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(e)}catch(e){throw console.error("getActiveCacheOperations error:",e),e}}static async clearCache(e=null){return e?await this.removeServiceFromLogs(e):await this.clearAllCache()}static async resetDatabase(){try{const e=await fetch(`${u}/management/database`,{method:"DELETE",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(6e4)});return await this.handleResponse(e)}catch(e){throw console.error("resetDatabase error:",e),e}}static async resetLogPosition(){try{const e=await fetch(`${u}/management/reset-logs`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(6e4)});return await this.handleResponse(e)}catch(e){throw console.error("resetLogPosition error:",e),e}}static async processAllLogs(){try{const e=await fetch(`${u}/management/process-all-logs`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(12e4)});return await this.handleResponse(e)}catch(e){throw console.error("processAllLogs error:",e),e}}static async cancelProcessing(){try{const e=await fetch(`${u}/management/cancel-processing`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),signal:AbortSignal.timeout(1e4)});return await this.handleResponse(e)}catch(e){throw console.error("cancelProcessing error:",e),e}}static async getProcessingStatus(){try{const e=await fetch(`${u}/management/processing-status`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(e)}catch(e){throw console.error("getProcessingStatus error:",e),e}}static async removeServiceFromLogs(e){try{const r=await fetch(`${u}/management/logs/remove-service`,{method:"POST",headers:this.getHeaders({"Content-Type":"application/json"}),body:JSON.stringify({service:e}),signal:AbortSignal.timeout(12e4)});return await this.handleResponse(r)}catch(r){throw console.error("removeServiceFromLogs error:",r),r}}static async getServiceLogCounts(){try{const e=await fetch(`${u}/management/logs/service-counts`,{signal:AbortSignal.timeout(3e4),headers:this.getHeaders()});return await this.handleResponse(e)}catch(e){throw console.error("getServiceLogCounts error:",e),e}}static async getConfig(){try{const e=await fetch(`${u}/management/config`,{signal:AbortSignal.timeout(5e3),headers:this.getHeaders()});return await this.handleResponse(e)}catch(e){return console.error("getConfig error:",e),{cachePath:"/cache",logPath:"/logs/access.log",services:["steam","epic","origin","blizzard","wsus","riot"]}}}}class Q{static generateMockData(e=50){const r=["192.168.1.100","192.168.1.101","192.168.1.102","192.168.1.103","192.168.1.104","192.168.1.105","192.168.1.106","192.168.1.107","192.168.1.108","192.168.1.109","192.168.1.110","192.168.1.111","10.0.0.50","10.0.0.51","10.0.0.52","10.0.0.53","127.0.0.1"],a=[{name:"Counter-Strike 2",size:32212254720},{name:"Dota 2",size:37580963840},{name:"Team Fortress 2",size:26843545600},{name:"Grand Theft Auto V",size:102005473280},{name:"Apex Legends",size:64424509440},{name:"Dead by Daylight",size:48318382080},{name:"Marvel Rivals",size:59055800320},{name:"Path of Exile",size:42949672960},{name:"Warframe",size:53687091200},{name:"Destiny 2",size:112742891520},{name:"Rust",size:37580963840},{name:"Valheim",size:1073741824},{name:"Unknown Steam Game",size:16106127360}],t={totalCacheSize:2e12,usedCacheSize:145e10,freeCacheSize:55e10,usagePercent:72.5,totalFiles:48293+(typeof e=="number"?e:500)*100,serviceSizes:{steam:65e10,epic:32e10,origin:18e10,blizzard:15e10,wsus:1e11,riot:5e10}},s=[],n=new Date,i=e==="unlimited"?500:e,c={};for(let l=0;l<i;l++){const h=N[Math.floor(Math.random()*N.length)],f=r[Math.floor(Math.random()*r.length)],w=Math.random()<.3,H=Math.pow(l/i,2)*2160,g=new Date(n.getTime()-H*60*60*1e3-Math.random()*36e5);let S;if(w){const I=new Date(g.getTime()+Math.random()*5e3);S={id:l+1,service:h,clientIp:f,startTime:g.toISOString(),endTime:I.toISOString(),cacheHitBytes:0,cacheMissBytes:0,totalBytes:0,cacheHitPercent:0,isActive:!1,gameName:null}}else{let I=null,E;if(h==="steam"&&Math.random()<.7){const P=a[Math.floor(Math.random()*a.length)];I=P.name,E=Math.floor(P.size*(.8+Math.random()*.2))}else E=Math.floor(Math.random()*50*1024*1024*1024);const T=Math.min(.95,.1+H/2160*.85),M=Math.floor(E*T),V=E-M,W=T>.8?500*1024*1024:50*1024*1024,F=E/W*1e3,X=new Date(g.getTime()+F);S={id:l+1,service:h,clientIp:f,startTime:g.toISOString(),endTime:X.toISOString(),cacheHitBytes:M,cacheMissBytes:V,totalBytes:E,cacheHitPercent:M/E*100,isActive:l<3&&H<.5,gameName:I,gameAppId:I&&I!=="Unknown Steam Game"?2e5+Math.floor(Math.random()*2e6):null}}c[f]||(c[f]={totalCacheHitBytes:0,totalCacheMissBytes:0,totalDownloads:0,lastSeen:g}),c[f].totalCacheHitBytes+=S.cacheHitBytes||0,c[f].totalCacheMissBytes+=S.cacheMissBytes||0,c[f].totalDownloads+=1,g>c[f].lastSeen&&(c[f].lastSeen=g),s.push(S)}s.sort((l,h)=>new Date(h.startTime).getTime()-new Date(l.startTime).getTime());const v=r.map(l=>{const h=c[l];if(h){const f=h.totalCacheHitBytes+h.totalCacheMissBytes;return{clientIp:l,totalCacheHitBytes:h.totalCacheHitBytes,totalCacheMissBytes:h.totalCacheMissBytes,totalBytes:f,cacheHitPercent:f>0?h.totalCacheHitBytes/f*100:0,totalDownloads:h.totalDownloads,lastSeen:h.lastSeen.toISOString()}}else return{clientIp:l,totalCacheHitBytes:0,totalCacheMissBytes:0,totalBytes:0,cacheHitPercent:0,totalDownloads:0,lastSeen:null}}).filter(l=>l.totalBytes>0),x=N.map(l=>{var H;const h=s.filter(g=>g.service===l),f=h.reduce((g,S)=>g+S.cacheHitBytes,0),w=h.reduce((g,S)=>g+S.cacheMissBytes,0);return{service:l,totalCacheHitBytes:f||t.serviceSizes[l]*.8,totalCacheMissBytes:w||t.serviceSizes[l]*.2,totalBytes:f+w||t.serviceSizes[l],cacheHitPercent:f+w>0?f/(f+w)*100:80,totalDownloads:h.length,lastActivity:((H=h[0])==null?void 0:H.startTime)||new Date(n.getTime()-Math.random()*72e5).toISOString()}});return{cacheInfo:t,activeDownloads:s.filter(l=>l.isActive),latestDownloads:s,clientStats:v,serviceStats:x}}static generateRealtimeUpdate(){const e=["192.168.1.100","192.168.1.101","192.168.1.102","192.168.1.103","192.168.1.104","192.168.1.105","192.168.1.106","192.168.1.107"];if(Math.random()<.2)return{id:Date.now(),service:N[Math.floor(Math.random()*N.length)],clientIp:e[Math.floor(Math.random()*e.length)],startTime:new Date().toISOString(),endTime:new Date().toISOString(),cacheHitBytes:0,cacheMissBytes:0,totalBytes:0,cacheHitPercent:0,isActive:!1};const a=Math.floor(Math.random()*5e8),t=Math.floor(Math.random()*1e8);return{id:Date.now(),service:N[Math.floor(Math.random()*N.length)],clientIp:e[Math.floor(Math.random()*e.length)],startTime:new Date().toISOString(),endTime:null,cacheHitBytes:a,cacheMissBytes:t,totalBytes:a+t,cacheHitPercent:a/(a+t)*100,isActive:!0,gameName:Math.random()<.5?"Counter-Strike 2":null}}}const ee={},oe=d.createContext(void 0),Ye=()=>{const m=d.useContext(oe);if(!m)throw new Error("useData must be used within DataProvider");return m},Ee=({children:m})=>{const[e,r]=d.useState(!1),[a,t]=d.useState(20),[s,n]=d.useState(20),[i,c]=d.useState(null),[v,x]=d.useState([]),[l,h]=d.useState([]),[f,w]=d.useState([]),[H,g]=d.useState([]),[S,I]=d.useState(!0),[E,T]=d.useState(null),[M,V]=d.useState(!1),[W,F]=d.useState(null),[X,P]=d.useState("checking"),O=d.useRef(!0),R=d.useRef(!1),Y=d.useRef(!1),b=d.useRef(null),C=d.useRef(null),se=()=>{if(!(typeof import.meta<"u"&&(ee!=null&&ee.VITE_API_URL)))return""},ne=async()=>{try{const y=se();return(await fetch(`${y}/health`,{signal:AbortSignal.timeout(5e3)})).ok?(P("connected"),!0):(P("error"),!1)}catch{return P("disconnected"),!1}},U=async()=>{if(!(Y.current&&!O.current)){Y.current=!0,b.current&&b.current.abort(),b.current=new AbortController;try{if(O.current&&I(!0),e){const y=a==="unlimited"?100:Math.min(Number(a),100),p=Q.generateMockData(y);c(p.cacheInfo),x(p.activeDownloads),h(p.latestDownloads),w(p.clientStats),g(p.serviceStats),T(null),P("connected"),R.current=!0}else if(await ne())try{const K=setTimeout(()=>{var B;return(B=b.current)==null?void 0:B.abort()},M?3e4:1e4);if(O.current){const[B,A]=await Promise.all([D.getCacheInfo(b.current.signal),D.getActiveDownloads(b.current.signal)]);B&&c(B),A&&x(A);const k=await D.getLatestDownloads(b.current.signal,20);k&&(h(k),R.current=!0),setTimeout(async()=>{var $;if(!(($=b.current)!=null&&$.signal.aborted))try{const[j,_]=await Promise.all([D.getClientStats(b.current.signal),D.getServiceStats(b.current.signal)]);j&&w(j),_&&g(_)}catch(j){console.log("Deferred stats fetch error:",j)}},100)}else{const B=s==="unlimited"?100:s,[A,k,$,j,_]=await Promise.allSettled([D.getCacheInfo(b.current.signal),D.getActiveDownloads(b.current.signal),D.getLatestDownloads(b.current.signal,B),D.getClientStats(b.current.signal),D.getServiceStats(b.current.signal)]);A.status==="fulfilled"&&A.value!==void 0&&c(A.value),k.status==="fulfilled"&&k.value!==void 0&&x(k.value),$.status==="fulfilled"&&$.value!==void 0&&(h($.value),R.current=!0,M&&$.value.length>0&&F(he=>({...he,message:`Processing logs... Found ${$.value.length} downloads`,downloadCount:$.value.length}))),j.status==="fulfilled"&&j.value!==void 0&&w(j.value),_.status==="fulfilled"&&_.value!==void 0&&g(_.value)}clearTimeout(K),T(null)}catch(p){R.current||(p.name==="AbortError"?T("Request timeout - the server may be busy"):T("Failed to fetch data from API"))}else R.current||T("Cannot connect to API server")}catch(y){console.error("Error in fetchData:",y),!R.current&&!e&&T("An unexpected error occurred")}finally{O.current&&(I(!1),O.current=!1),Y.current=!1}}},ce=y=>{if(e){const p=y==="unlimited"?100:Math.min(y,100);t(p)}},ie=y=>{n(y==="unlimited"?100:y)},te=()=>M?15e3:s==="unlimited"||s>100?3e4:Ae;d.useEffect(()=>{if(!e){U();const y=te();return C.current=setInterval(U,y),()=>{C.current&&(clearInterval(C.current),C.current=null),b.current&&b.current.abort()}}},[M,e,s]),d.useEffect(()=>{if(e){C.current&&(clearInterval(C.current),C.current=null),c(null),x([]),h([]),w([]),g([]);const y=a==="unlimited"?100:Math.min(Number(a),100),p=Q.generateMockData(y);c(p.cacheInfo),x(p.activeDownloads),h(p.latestDownloads),w(p.clientStats),g(p.serviceStats),T(null),P("connected"),R.current=!0;const K=3e4;return C.current=setInterval(()=>{const B=Q.generateRealtimeUpdate();h(A=>[B,...A.slice(0,99)]),x(A=>[B,...A.filter($=>$.id!==B.id)].slice(0,5))},K),()=>{C.current&&(clearInterval(C.current),C.current=null)}}else c(null),x([]),h([]),w([]),g([]),T(null),R.current=!1,O.current=!0,U()},[e,a]),d.useEffect(()=>()=>{C.current&&clearInterval(C.current),b.current&&b.current.abort()},[]);const le={mockMode:e,setMockMode:r,mockDownloadCount:a,updateMockDataCount:ce,apiDownloadCount:s,updateApiDownloadCount:ie,cacheInfo:i,activeDownloads:v,latestDownloads:l,clientStats:f,serviceStats:H,loading:S,error:E,fetchData:U,clearAllData:()=>{c(null),x([]),h([]),w([]),g([]),R.current=!1},isProcessingLogs:M,setIsProcessingLogs:V,processingStatus:W,setProcessingStatus:F,connectionStatus:X,getCurrentRefreshInterval:te};return o.jsx(oe.Provider,{value:le,children:m})},Me=({title:m="LANCache Manager",subtitle:e="High-performance cache monitoring & management",connectionStatus:r="connected"})=>{const t=(()=>{switch(r){case"connected":return{color:"cache-hit",text:"Connected",icon:o.jsx(q,{className:"w-4 h-4"})};case"disconnected":return{color:"text-themed-error",text:"Disconnected",icon:o.jsx(q,{className:"w-4 h-4"})};case"reconnecting":return{color:"cache-miss",text:"Reconnecting...",icon:o.jsx(q,{className:"w-4 h-4 animate-pulse"})};default:return{color:"text-themed-muted",text:"Unknown",icon:o.jsx(q,{className:"w-4 h-4"})}}})();return o.jsx("header",{className:"border-b",style:{backgroundColor:"var(--theme-nav-bg)",borderColor:"var(--theme-nav-border)"},children:o.jsx("div",{className:"container mx-auto px-4",children:o.jsxs("div",{className:"flex items-center justify-between h-16 min-w-0",children:[o.jsxs("div",{className:"flex items-center space-x-2 sm:space-x-3 min-w-0 flex-1",children:[o.jsx("div",{className:"p-1.5 sm:p-2 rounded-lg flex-shrink-0",style:{backgroundColor:"var(--theme-icon-blue)"},children:o.jsx(ue,{className:"w-5 h-5 sm:w-6 sm:h-6 text-white"})}),o.jsxs("div",{className:"min-w-0 flex-1",children:[o.jsx("h1",{className:"text-lg sm:text-xl font-bold text-themed-primary truncate",children:m}),o.jsx("p",{className:"text-xs sm:text-sm text-themed-muted truncate hidden sm:block",children:e})]})]}),o.jsx("div",{className:"flex items-center space-x-1 sm:space-x-2 flex-shrink-0",children:o.jsxs("div",{className:`flex items-center space-x-1 ${t.color}`,children:[t.icon,o.jsx("span",{className:"text-xs sm:text-sm font-medium hidden sm:inline",children:t.text})]})})]})})})},Re=({activeTab:m,setActiveTab:e})=>{var n;const[r,a]=d.useState(!1),t=[{id:"dashboard",label:"Dashboard",icon:fe},{id:"downloads",label:"Downloads",icon:ge},{id:"clients",label:"Clients",icon:be},{id:"management",label:"Management",icon:ve}],s=({tab:i,isActive:c,onClick:v,className:x=""})=>{const l=i.icon;return o.jsxs("button",{onClick:v,className:`flex items-center space-x-2 px-3 py-2 rounded-lg font-medium transition-all duration-200 ${x}`,style:{color:c?"var(--theme-nav-tab-active)":"var(--theme-nav-tab-inactive)",backgroundColor:"transparent"},onMouseEnter:h=>{c||(h.currentTarget.style.color="var(--theme-nav-tab-hover)",h.currentTarget.style.backgroundColor="var(--theme-nav-mobile-item-hover)")},onMouseLeave:h=>{c||(h.currentTarget.style.color="var(--theme-nav-tab-inactive)",h.currentTarget.style.backgroundColor="transparent")},children:[o.jsx(l,{className:"w-5 h-5"}),o.jsx("span",{children:i.label}),c&&o.jsx("div",{className:"absolute bottom-0 left-0 right-0 h-0.5 rounded-full",style:{backgroundColor:"var(--theme-nav-tab-active-border)"}})]})};return o.jsx("nav",{className:"border-b",style:{backgroundColor:"var(--theme-nav-bg)",borderColor:"var(--theme-nav-border)"},children:o.jsxs("div",{className:"container mx-auto px-4",children:[o.jsx("div",{className:"hidden md:flex space-x-1 h-12 items-center",children:t.map(i=>o.jsx("div",{className:"relative",children:o.jsx(s,{tab:i,isActive:m===i.id,onClick:()=>e(i.id)})},i.id))}),o.jsxs("div",{className:"md:hidden",children:[o.jsxs("div",{className:"flex items-center justify-between h-12",children:[o.jsx("div",{className:"flex items-center space-x-2",children:o.jsx("span",{className:"font-medium text-themed-primary",children:((n=t.find(i=>i.id===m))==null?void 0:n.label)||"Dashboard"})}),o.jsx("button",{onClick:()=>a(!r),className:"p-2 rounded-lg transition-colors",style:{color:"var(--theme-nav-tab-inactive)",backgroundColor:"transparent"},onMouseEnter:i=>{i.currentTarget.style.backgroundColor="var(--theme-nav-mobile-item-hover)"},onMouseLeave:i=>{i.currentTarget.style.backgroundColor="transparent"},children:r?o.jsx(pe,{className:"w-5 h-5"}):o.jsx(ye,{className:"w-5 h-5"})})]}),r&&o.jsx("div",{className:"border-t py-2 space-y-1",style:{backgroundColor:"var(--theme-nav-mobile-menu-bg)",borderColor:"var(--theme-nav-border)"},children:t.map(i=>o.jsx(s,{tab:i,isActive:m===i.id,onClick:()=>{e(i.id),a(!1)},className:"w-full justify-start"},i.id))})]})]})})};class He extends d.Component{constructor(e){super(e),this.state={hasError:!1}}static getDerivedStateFromError(e){return{hasError:!0,error:e}}componentDidCatch(e,r){console.error("Error caught by boundary:",e,r)}render(){var e;return this.state.hasError?o.jsx("div",{className:"min-h-screen bg-themed-primary flex items-center justify-center p-4",children:o.jsx("div",{className:"alert-error rounded-lg p-6 max-w-md w-full",children:o.jsxs("div",{className:"flex items-start space-x-3",children:[o.jsx(xe,{className:"w-5 h-5 mt-0.5"}),o.jsxs("div",{children:[o.jsx("h3",{className:"text-lg font-semibold mb-2",children:"Something went wrong"}),o.jsx("p",{className:"text-sm text-themed-secondary",children:((e=this.state.error)==null?void 0:e.message)||"An unexpected error occurred"}),o.jsx("button",{onClick:()=>window.location.reload(),className:"mt-4 px-4 py-2 action-delete rounded-lg text-sm smooth-transition",children:"Reload Page"})]})]})})}):this.props.children}}const Pe=({message:m,size:e="md",fullScreen:r=!1})=>{const a={xs:"w-4 h-4",sm:"w-6 h-6",md:"w-8 h-8",lg:"w-12 h-12",xl:"w-16 h-16"},t=o.jsxs("div",{className:"flex flex-col items-center justify-center space-y-4",children:[o.jsx(we,{className:`${a[e]} text-themed-primary animate-spin`}),m&&o.jsx("p",{className:"text-sm text-themed-muted",children:m})]});return r?o.jsx("div",{className:"fixed inset-0 bg-themed-primary bg-opacity-50 flex items-center justify-center z-50",children:t}):o.jsx("div",{className:"flex items-center justify-center min-h-[200px]",children:t})},ae=d.lazy(()=>z(()=>import("./Dashboard-B2-YEbMz.js"),__vite__mapDeps([0,1,2,3,4,5,6]))),ke=d.lazy(()=>z(()=>import("./DownloadsTab-Cu2PMXAj.js"),__vite__mapDeps([7,1,2,3,4,8]))),je=d.lazy(()=>z(()=>import("./ClientsTab-BP8BuIRS.js"),__vite__mapDeps([9,1,2,3,4,6]))),Ne=d.lazy(()=>z(()=>import("./ServicesTab-CDsKH870.js"),__vite__mapDeps([10,1,2,3,4,6]))),Oe=d.lazy(()=>z(()=>import("./ManagementTab-DqorkMH0.js"),__vite__mapDeps([11,1,2,3,4,8,12]))),_e=()=>{const[m,e]=d.useState("dashboard"),r=()=>{const a=(()=>{switch(m){case"dashboard":return ae;case"downloads":return ke;case"clients":return je;case"services":return Ne;case"management":return Oe;default:return ae}})();return o.jsx(d.Suspense,{fallback:o.jsx(Pe,{fullScreen:!1,message:"Loading..."}),children:o.jsx(a,{})})};return o.jsx(He,{children:o.jsx(Ee,{children:o.jsxs("div",{className:"min-h-screen",style:{backgroundColor:"var(--theme-bg-primary)",color:"var(--theme-text-primary)"},children:[o.jsx(Me,{}),o.jsx(Re,{activeTab:m,setActiveTab:e}),o.jsx("main",{className:"container mx-auto px-4 py-6",children:r()})]})})})};class Le{constructor(){L(this,"currentTheme",null);L(this,"styleElement",null)}async loadThemes(){const e=this.getBuiltInThemes(),r=[],a=[];try{const n=await fetch(`${u}/theme`);if(n.ok){const i=await n.json();for(const c of i)if(c.format==="toml")try{const v=await fetch(`${u}/theme/${c.id}`);if(v.status===404){a.push(c.id),console.log(`Theme ${c.id} no longer exists on server`);continue}if(v.ok){const x=await v.text(),l=this.parseTomlTheme(x);l&&r.push(l)}}catch(v){console.error(`Failed to load theme ${c.id}:`,v)}if(a.length>0&&this.currentTheme&&a.includes(this.currentTheme.meta.id)){console.log(`Current theme ${this.currentTheme.meta.id} was deleted, resetting to default`);const c=e.find(v=>v.meta.id==="dark-default");c&&this.applyTheme(c)}}}catch(n){console.error("Failed to load themes from server:",n)}const t=[...e],s=new Set(t.map(n=>n.meta.id));return r.forEach(n=>{s.has(n.meta.id)||(t.push(n),s.add(n.meta.id))}),t}getBuiltInThemes(){return[{meta:{id:"dark-default",name:"Dark Default",description:"Default dark theme with blue accents",author:"System",version:"1.0.0",isDark:!0},colors:{primaryColor:"#3b82f6",secondaryColor:"#8b5cf6",accentColor:"#06b6d4",bgPrimary:"#111827",bgSecondary:"#1f2937",bgTertiary:"#374151",bgHover:"#4b5563",textPrimary:"#ffffff",textSecondary:"#d1d5db",textMuted:"#9ca3af",textAccent:"#60a5fa",dragHandleColor:"#6b7280",dragHandleHover:"#60a5fa",borderPrimary:"#374151",borderSecondary:"#4b5563",borderFocus:"#3b82f6",navBg:"#1f2937",navBorder:"#374151",navTabActive:"#3b82f6",navTabInactive:"#9ca3af",navTabHover:"#ffffff",navTabActiveBorder:"#3b82f6",navMobileMenuBg:"#1f2937",navMobileItemHover:"#374151",success:"#10b981",successBg:"#064e3b",successText:"#34d399",warning:"#fb923c",warningBg:"#44403c",warningText:"#fcd34d",error:"#ef4444",errorBg:"#7f1d1d",errorText:"#fca5a5",info:"#3b82f6",infoBg:"#1e3a8a",infoText:"#93c5fd",steamColor:"#3b82f6",epicColor:"#8b5cf6",originColor:"#10b981",blizzardColor:"#ef4444",wsusColor:"#06b6d4",riotColor:"#f59e0b",cardBg:"#1f2937",cardBorder:"#374151",buttonBg:"#3b82f6",buttonHover:"#2563eb",buttonText:"#ffffff",inputBg:"#374151",inputBorder:"#4b5563",inputFocus:"#3b82f6",badgeBg:"#3b82f6",badgeText:"#ffffff",progressBar:"#3b82f6",progressBg:"#374151",hitRateHighBg:"#064e3b",hitRateHighText:"#34d399",hitRateMediumBg:"#1e3a8a",hitRateMediumText:"#93c5fd",hitRateLowBg:"#44403c",hitRateLowText:"#fbbf24",hitRateWarningBg:"#44403c",hitRateWarningText:"#fcd34d",actionResetBg:"#f59e0b",actionResetHover:"#d97706",actionProcessBg:"#10b981",actionProcessHover:"#059669",actionDeleteBg:"#ef4444",actionDeleteHover:"#dc2626",iconBgBlue:"#3b82f6",iconBgGreen:"#10b981",iconBgEmerald:"#10b981",iconBgPurple:"#8b5cf6",iconBgIndigo:"#6366f1",iconBgOrange:"#f97316",iconBgYellow:"#eab308",iconBgCyan:"#06b6d4",iconBgRed:"#ef4444",chartColor1:"#3b82f6",chartColor2:"#10b981",chartColor3:"#f59e0b",chartColor4:"#ef4444",chartColor5:"#8b5cf6",chartColor6:"#06b6d4",chartColor7:"#f97316",chartColor8:"#ec4899",chartBorderColor:"#1f2937",chartGridColor:"#374151",chartTextColor:"#9ca3af",chartCacheHitColor:"#10b981",chartCacheMissColor:"#f59e0b",scrollbarTrack:"#374151",scrollbarThumb:"#6B7280",scrollbarHover:"#9CA3AF"}},{meta:{id:"light-default",name:"Light Default",description:"Default light theme with blue accents",author:"System",version:"1.0.0",isDark:!1},colors:{primaryColor:"#3b82f6",secondaryColor:"#8b5cf6",accentColor:"#06b6d4",bgPrimary:"#f8f9fa",bgSecondary:"#ffffff",bgTertiary:"#f3f4f6",bgHover:"#e5e7eb",textPrimary:"#111827",textSecondary:"#374151",textMuted:"#6b7280",textAccent:"#2563eb",dragHandleColor:"#9ca3af",dragHandleHover:"#2563eb",borderPrimary:"#e5e7eb",borderSecondary:"#d1d5db",borderFocus:"#3b82f6",navBg:"#ffffff",navBorder:"#e5e7eb",navTabActive:"#3b82f6",navTabInactive:"#6b7280",navTabHover:"#111827",navTabActiveBorder:"#3b82f6",navMobileMenuBg:"#ffffff",navMobileItemHover:"#f3f4f6",success:"#10b981",successBg:"#d1fae5",successText:"#047857",warning:"#f97316",warningBg:"#fef3c7",warningText:"#b45309",error:"#ef4444",errorBg:"#fee2e2",errorText:"#991b1b",info:"#3b82f6",infoBg:"#dbeafe",infoText:"#1e40af",steamColor:"#3b82f6",epicColor:"#8b5cf6",originColor:"#10b981",blizzardColor:"#ef4444",wsusColor:"#06b6d4",riotColor:"#f59e0b",cardBg:"#ffffff",cardBorder:"#e5e7eb",buttonBg:"#3b82f6",buttonHover:"#2563eb",buttonText:"#ffffff",inputBg:"#ffffff",inputBorder:"#d1d5db",inputFocus:"#3b82f6",badgeBg:"#3b82f6",badgeText:"#ffffff",progressBar:"#3b82f6",progressBg:"#e5e7eb",hitRateHighBg:"#d1fae5",hitRateHighText:"#047857",hitRateMediumBg:"#dbeafe",hitRateMediumText:"#1e40af",hitRateLowBg:"#fef3c7",hitRateLowText:"#92400e",hitRateWarningBg:"#fef3c7",hitRateWarningText:"#92400e",actionResetBg:"#f59e0b",actionResetHover:"#d97706",actionProcessBg:"#10b981",actionProcessHover:"#059669",actionDeleteBg:"#ef4444",actionDeleteHover:"#dc2626",iconBgBlue:"#3b82f6",iconBgGreen:"#10b981",iconBgEmerald:"#10b981",iconBgPurple:"#8b5cf6",iconBgIndigo:"#6366f1",iconBgOrange:"#f97316",iconBgYellow:"#eab308",iconBgCyan:"#06b6d4",iconBgRed:"#ef4444",chartColor1:"#3b82f6",chartColor2:"#10b981",chartColor3:"#f59e0b",chartColor4:"#ef4444",chartColor5:"#8b5cf6",chartColor6:"#06b6d4",chartColor7:"#f97316",chartColor8:"#ec4899",chartBorderColor:"#e5e7eb",chartGridColor:"#d1d5db",chartTextColor:"#6b7280",chartCacheHitColor:"#047857",chartCacheMissColor:"#b45309",scrollbarTrack:"#e5e7eb",scrollbarThumb:"#9ca3af",scrollbarHover:"#6b7280"}}]}async getTheme(e){const r=this.getBuiltInThemes().find(a=>a.meta.id===e);if(r)return r;try{const a=await fetch(`${u}/theme/${e}`);if(!a.ok)return null;const t=await a.text();return this.parseTomlTheme(t)}catch(a){return console.error("Error loading theme:",a),null}}parseTomlTheme(e){try{const r=Te.parse(e);return!r.meta||!r.meta.id||!r.meta.name?(console.error("Invalid theme: missing meta.id or meta.name"),null):r.colors?r:(console.error("Invalid theme: missing colors section"),null)}catch(r){return console.error("Error parsing TOML theme:",r),null}}async uploadTheme(e){const r=await e.text(),a=this.parseTomlTheme(r);if(!a)throw new Error("Invalid TOML theme format");const t=new FormData;t.append("file",e);try{const s=await fetch(`${u}/theme/upload`,{method:"POST",headers:G.getAuthHeaders(),body:t});if(!s.ok){const n=await s.json().catch(()=>({error:"Failed to upload theme"}));throw new Error(n.error||"Failed to upload theme")}return a}catch(s){throw s.message.includes("Failed to fetch")||s.message.includes("NetworkError")?new Error("Cannot save theme: API server is not running. Please start the LANCache Manager API service."):s}}async deleteTheme(e){const r=await fetch(`${u}/theme/${e}`,{method:"DELETE",headers:G.getAuthHeaders()});if(!r.ok&&r.status!==404){const a=await r.json().catch(()=>({error:"Failed to delete theme"}));throw new Error(a.error||"Failed to delete theme")}}applyDefaultVariables(){const e=`
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
    `;let r=document.getElementById("lancache-default-vars");r||(r=document.createElement("style"),r.id="lancache-default-vars",document.head.appendChild(r)),r.textContent=e}clearTheme(){this.styleElement&&(this.styleElement.remove(),this.styleElement=null);const e=document.documentElement;e.removeAttribute("data-theme"),e.removeAttribute("data-theme-id"),this.currentTheme=null,localStorage.removeItem("lancache_selected_theme"),localStorage.removeItem("lancache_theme_css"),localStorage.removeItem("lancache_theme_dark"),this.applyDefaultVariables()}applyTheme(e){var i;if(!e||!e.colors)return;this.styleElement&&(this.styleElement.remove(),this.styleElement=null);const r=document.getElementById("lancache-theme-preload");r&&r.remove();const a=document.getElementById("lancache-default-preload");a&&a.remove();const t=e.colors,s=`
    :root {
      --theme-primary: ${t.primaryColor||"#3b82f6"};
      --theme-secondary: ${t.secondaryColor||"#8b5cf6"};
      --theme-accent: ${t.accentColor||"#06b6d4"};
      --theme-bg-primary: ${t.bgPrimary||"#111827"};
      --theme-bg-secondary: ${t.bgSecondary||"#1f2937"};
      --theme-bg-tertiary: ${t.bgTertiary||"#374151"};
      --theme-bg-hover: ${t.bgHover||"#4b5563"};
      --theme-text-primary: ${t.textPrimary||"#ffffff"};
      --theme-text-secondary: ${t.textSecondary||"#d1d5db"};
      --theme-text-muted: ${t.textMuted||"#9ca3af"};
      --theme-text-accent: ${t.textAccent||"#60a5fa"};
      --theme-drag-handle: ${t.dragHandleColor||t.textMuted||"#6b7280"};
      --theme-drag-handle-hover: ${t.dragHandleHover||t.textAccent||"#60a5fa"};
      --theme-border-primary: ${t.borderPrimary||"#374151"};
      --theme-border-secondary: ${t.borderSecondary||"#4b5563"};
      --theme-border-focus: ${t.borderFocus||"#3b82f6"};
      
      /* Navigation Variables */
      --theme-nav-bg: ${t.navBg||t.bgSecondary||"#1f2937"};
      --theme-nav-border: ${t.navBorder||t.borderPrimary||"#374151"};
      --theme-nav-tab-active: ${t.navTabActive||t.primaryColor||"#3b82f6"};
      --theme-nav-tab-inactive: ${t.navTabInactive||t.textMuted||"#9ca3af"};
      --theme-nav-tab-hover: ${t.navTabHover||t.textPrimary||"#ffffff"};
      --theme-nav-tab-active-border: ${t.navTabActiveBorder||t.primaryColor||"#3b82f6"};
      --theme-nav-mobile-menu-bg: ${t.navMobileMenuBg||t.bgSecondary||"#1f2937"};
      --theme-nav-mobile-item-hover: ${t.navMobileItemHover||t.bgTertiary||"#374151"};
      
      /* Status Colors */
      --theme-success: ${t.success||"#10b981"};
      --theme-success-bg: ${t.successBg||"#064e3b"};
      --theme-success-text: ${t.successText||"#34d399"};
      --theme-warning: ${t.warning||"#f59e0b"};
      --theme-warning-bg: ${t.warningBg||"#78350f"};
      --theme-warning-text: ${t.warningText||"#fbbf24"};
      --theme-error: ${t.error||"#ef4444"};
      --theme-error-bg: ${t.errorBg||"#7f1d1d"};
      --theme-error-text: ${t.errorText||"#fca5a5"};
      --theme-info: ${t.info||"#3b82f6"};
      --theme-info-bg: ${t.infoBg||"#1e3a8a"};
      --theme-info-text: ${t.infoText||"#93c5fd"};
      
      /* Service Colors */
      --theme-steam: ${t.steamColor||"#3b82f6"};
      --theme-epic: ${t.epicColor||"#8b5cf6"};
      --theme-origin: ${t.originColor||"#10b981"};
      --theme-blizzard: ${t.blizzardColor||"#ef4444"};
      --theme-wsus: ${t.wsusColor||"#06b6d4"};
      --theme-riot: ${t.riotColor||"#f59e0b"};
      
      /* Component Colors */
      --theme-card-bg: ${t.cardBg||t.bgSecondary||"#1f2937"};
      --theme-card-border: ${t.cardBorder||t.borderPrimary||"#374151"};
      --theme-button-bg: ${t.buttonBg||t.primaryColor||"#3b82f6"};
      --theme-button-hover: ${t.buttonHover||"#2563eb"};
      --theme-button-text: ${t.buttonText||"#ffffff"};
      --theme-input-bg: ${t.inputBg||t.bgTertiary||"#374151"};
      --theme-input-border: ${t.inputBorder||t.borderSecondary||"#4b5563"};
      --theme-input-focus: ${t.inputFocus||t.primaryColor||"#3b82f6"};
      --theme-badge-bg: ${t.badgeBg||t.primaryColor||"#3b82f6"};
      --theme-badge-text: ${t.badgeText||"#ffffff"};
      --theme-progress-bar: ${t.progressBar||t.primaryColor||"#3b82f6"};
      --theme-progress-bg: ${t.progressBg||t.bgTertiary||"#374151"};
      
      /* Hit Rate Colors - FIXED WITH PRETTIER COLORS */
      --theme-hit-rate-high-bg: ${t.hitRateHighBg||"#064e3b"};
      --theme-hit-rate-high-text: ${t.hitRateHighText||"#34d399"};
      --theme-hit-rate-medium-bg: ${t.hitRateMediumBg||"#1e3a8a"};
      --theme-hit-rate-medium-text: ${t.hitRateMediumText||"#93c5fd"};
      --theme-hit-rate-low-bg: ${t.hitRateLowBg||"#44403c"};
      --theme-hit-rate-low-text: ${t.hitRateLowText||"#fbbf24"};
      --theme-hit-rate-warning-bg: ${t.hitRateWarningBg||"#44403c"};
      --theme-hit-rate-warning-text: ${t.hitRateWarningText||"#fcd34d"};
      
      /* Action Button Colors */
      --theme-action-reset-bg: ${t.actionResetBg||"#f59e0b"};
      --theme-action-reset-hover: ${t.actionResetHover||"#d97706"};
      --theme-action-process-bg: ${t.actionProcessBg||"#10b981"};
      --theme-action-process-hover: ${t.actionProcessHover||"#059669"};
      --theme-action-delete-bg: ${t.actionDeleteBg||"#ef4444"};
      --theme-action-delete-hover: ${t.actionDeleteHover||"#dc2626"};
      
      /* Icon Colors */
      --theme-icon-blue: ${t.iconBgBlue||"#3b82f6"};
      --theme-icon-green: ${t.iconBgGreen||"#10b981"};
      --theme-icon-emerald: ${t.iconBgEmerald||"#10b981"};
      --theme-icon-purple: ${t.iconBgPurple||"#8b5cf6"};
      --theme-icon-indigo: ${t.iconBgIndigo||"#6366f1"};
      --theme-icon-orange: ${t.iconBgOrange||"#f97316"};
      --theme-icon-yellow: ${t.iconBgYellow||"#eab308"};
      --theme-icon-cyan: ${t.iconBgCyan||"#06b6d4"};
      --theme-icon-red: ${t.iconBgRed||"#ef4444"};
      
      /* Chart Colors */
      --theme-chart-1: ${t.chartColor1||"#3b82f6"};
      --theme-chart-2: ${t.chartColor2||"#10b981"};
      --theme-chart-3: ${t.chartColor3||"#f59e0b"};
      --theme-chart-4: ${t.chartColor4||"#ef4444"};
      --theme-chart-5: ${t.chartColor5||"#8b5cf6"};
      --theme-chart-6: ${t.chartColor6||"#06b6d4"};
      --theme-chart-7: ${t.chartColor7||"#f97316"};
      --theme-chart-8: ${t.chartColor8||"#ec4899"};
      --theme-chart-border: ${t.chartBorderColor||"#1f2937"};
      --theme-chart-grid: ${t.chartGridColor||"#374151"};
      --theme-chart-text: ${t.chartTextColor||"#9ca3af"};
      --theme-chart-cache-hit: ${t.chartCacheHitColor||"#10b981"};
      --theme-chart-cache-miss: ${t.chartCacheMissColor||"#f59e0b"};
      
      /* Scrollbar Colors */
      --theme-scrollbar-track: ${t.scrollbarTrack||t.bgTertiary||"#374151"};
      --theme-scrollbar-thumb: ${t.scrollbarThumb||t.textMuted||"#6B7280"};
      --theme-scrollbar-hover: ${t.scrollbarHover||t.textSecondary||"#9CA3AF"};
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
    ${((i=e.css)==null?void 0:i.content)||""}
  `;this.styleElement=document.createElement("style"),this.styleElement.id="lancache-theme",this.styleElement.textContent=s,document.head.appendChild(this.styleElement);const n=document.documentElement;n.setAttribute("data-theme",e.meta.isDark?"dark":"light"),n.setAttribute("data-theme-id",e.meta.id),this.currentTheme=e,localStorage.setItem("lancache_selected_theme",e.meta.id),localStorage.setItem("lancache_theme_css",s),localStorage.setItem("lancache_theme_dark",e.meta.isDark?"true":"false"),window.dispatchEvent(new Event("themechange"))}async loadSavedTheme(){const e=document.getElementById("lancache-theme-preload"),r=localStorage.getItem("lancache_selected_theme");if(e&&r){const t=await this.getTheme(r);if(t){this.applyTheme(t),this.currentTheme=t;return}localStorage.removeItem("lancache_selected_theme"),localStorage.removeItem("lancache_theme_css"),localStorage.removeItem("lancache_theme_dark")}if(this.applyDefaultVariables(),r){const t=await this.getTheme(r);if(t){this.applyTheme(t);return}}const a=await this.getTheme("dark-default");a&&this.applyTheme(a)}getCurrentThemeId(){var e;return((e=this.currentTheme)==null?void 0:e.meta.id)||"dark-default"}getCurrentTheme(){return this.currentTheme}isThemeApplied(){return this.currentTheme!==null}exportTheme(e){var a;console.log("Exporting theme to TOML:",e);let r="";return r+=`[meta]
`,r+=`name = "${e.meta.name}"
`,r+=`id = "${e.meta.id}"
`,e.meta.description&&(r+=`description = "${e.meta.description}"
`),e.meta.author&&(r+=`author = "${e.meta.author}"
`),e.meta.version&&(r+=`version = "${e.meta.version}"
`),e.meta.isDark!==void 0&&(r+=`isDark = ${e.meta.isDark}
`),r+=`
`,r+=`[colors]
`,e.colors&&(console.log("Exporting colors:",e.colors),Object.entries(e.colors).forEach(([t,s])=>{r+=`${t} = "${s}"
`})),r+=`
`,e.custom&&Object.keys(e.custom).length>0&&(r+=`[custom]
`,Object.entries(e.custom).forEach(([t,s])=>{r+=`"${t}" = "${s}"
`}),r+=`
`),(a=e.css)!=null&&a.content&&(r+=`[css]
`,r+=`content = """
${e.css.content}
"""
`),r}}const ze=new Le;ze.loadSavedTheme();Ce.createRoot(document.getElementById("root")).render(o.jsx(Se.StrictMode,{children:o.jsx(_e,{})}));export{D as A,We as F,Xe as S,G as a,u as b,ze as t,Ye as u};
//# sourceMappingURL=index-DNKXZaqj.js.map
