'use strict';
const assert=require('node:assert/strict');
require('../src/VehicleCounter.Api/wwwroot/activity.js');
const {Controller}=require('../src/VehicleCounter.Api/wwwroot/review.js');
class Element {
  constructor(){this.hidden=true;this.children=[];this.events={};this.dataset={};this.width=960;this.height=540;this.clientWidth=960;this.currentTime=0;this.duration=20;this.readyState=1;this.seeking=false;this.value='all';this.attributes={};this.style={};this.classList={toggle(){}};}
  addEventListener(name,handler){this.events[name]=handler;}
  removeEventListener(name){delete this.events[name];}
  append(child){this.children.push(child);}
  replaceChildren(){this.children=[];}
  setAttribute(name,value){this.attributes[name]=value;}
  removeAttribute(name){delete this.attributes[name];}
  pause(){} load(){} requestVideoFrameCallback(){return 1;} cancelVideoFrameCallback(){}
  scrollIntoView(){this.scrolled=true;}
  getContext(){return context;}
}
const rectangles=[];
const context={clearRect(){},beginPath(){},rect(){},fill(){},strokeRect(...args){rectangles.push(args);},fillRect(){},fillText(){},measureText(text){return {width:text.length*6};}};
const elements=new Map();
const document={getElementById(id){if(!elements.has(id))elements.set(id,new Element());return elements.get(id);},createElement(){return new Element();}};
global.ResizeObserver=class{observe(){}};
const fixture=id=>({jobId:'job',trackId:id,video:{width:1920,height:1080,fps:30},vehicle:{trackId:id,vehicleClass:'car',firstSeenSeconds:1,crossedAtSeconds:2},observations:[{sourceFrame:30,videoSeconds:1,box:{x:100,y:200,width:80,height:60}},{sourceFrame:60,videoSeconds:2,box:{x:200,y:300,width:100,height:80}}],videoUrl:'/api/jobs/job/video'});
(async()=>{
  let selected=null;
  global.fetch=async()=>({ok:true,json:async()=>fixture(7)});
  const controller=new Controller(document,{onSelect:id=>{selected=id;}});
  await controller.select({id:'job',settings:{frameStride:2}},fixture(7).vehicle);
  assert.equal(selected,7);
  assert.equal(document.getElementById('review-video').currentTime,2);
  assert.equal(document.getElementById('review-stage').hidden,false);
  assert.deepEqual(rectangles.at(-1),[100,150,50,40]);
  assert.equal(document.getElementById('viewport').scrolled,true);
  console.log('PASS: Selecting an ID seeks to its crossing and draws its own scaled highlight');
  controller.seek(1);
  assert.equal(document.getElementById('review-video').currentTime,1);
  assert.deepEqual(rectangles.at(-1),[50,100,40,30]);
  const count=rectangles.length;
  controller.draw(8);
  assert.equal(rectangles.length,count);
  console.log('PASS: First-seen navigation works and no box is drawn outside recorded appearances');
  let resolveOld;
  global.fetch=url=>url.includes('/1/review')?new Promise(resolve=>{resolveOld=()=>resolve({ok:true,json:async()=>fixture(1)});}):Promise.resolve({ok:true,json:async()=>fixture(2)});
  const old=controller.select({id:'job',settings:{frameStride:2}},fixture(1).vehicle);
  await controller.select({id:'job',settings:{frameStride:2}},fixture(2).vehicle);
  resolveOld();await old;
  assert.equal(controller.data.trackId,2);
  console.log('PASS: Rapid selection cannot show a stale vehicle response');
  let clicked=null;
  const activity=new FlowActivity.Controller(document,(vehicle)=>{clicked=vehicle.trackId;});
  activity.update({id:'job',playbackAvailable:true,vehicles:[{...fixture(10).vehicle},{...fixture(2).vehicle},{...fixture(1).vehicle}],events:[]});
  const firstButton=document.getElementById('events-body').children[0].children[0].children[0];
  assert.equal(firstButton.textContent,'#1');firstButton.events.click();assert.equal(clicked,1);
  activity.select(1);
  assert.equal(document.getElementById('events-body').children[0].className,'selected-vehicle-row');
  console.log('PASS: Ascending track buttons invoke vehicle selection and mark the selected row');
  controller.close();assert.equal(controller.active,false);assert.equal(document.getElementById('review-stage').hidden,true);
})().catch(error=>{console.error(error);process.exitCode=1;});

