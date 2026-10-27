'use strict';
const vm=require('node:vm');const fs=require('node:fs');const assert=require('node:assert/strict');
const calls=[];const storage=new Map();
const context={ROADLENS_CONFIG:{apiBaseUrl:'https://roadlens-test.onrender.com/'},localStorage:{getItem:key=>storage.get(key),setItem:(key,value)=>storage.set(key,value)},crypto:{randomUUID:()=> '11111111-1111-4111-8111-111111111111'},Headers,fetch:async(url,options)=>{calls.push({url,options});return{ok:true};}};
vm.runInNewContext(fs.readFileSync('src/VehicleCounter.Api/wwwroot/api.js','utf8'),context);
(async()=>{assert.equal(context.RoadLensApi.url('/api/jobs'),'https://roadlens-test.onrender.com/api/jobs');assert.equal(context.RoadLensApi.url('/api/jobs/id/video?ticket=abc'),'https://roadlens-test.onrender.com/api/jobs/id/video?ticket=abc');await context.RoadLensApi.fetch('/api/jobs',{method:'POST',body:'fixture'});assert.equal(calls[0].options.headers.get('X-RoadLens-Client'),'11111111-1111-4111-8111-111111111111');assert.equal(calls[0].options.body,'fixture');console.log('PASS: Remote API URLs, scoped media URLs, browser capability headers, and upload options');})().catch(e=>{console.error(e);process.exitCode=1});
