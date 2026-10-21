'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const {test} = require('node:test');
const file = path.join(__dirname, '../../tools/sdk/nodejs/p2l.js');
const mod={exports:{}};
const mqttStub={};
vm.runInNewContext(fs.readFileSync(file,'utf8'),{require:n=>n==='mqtt'?mqttStub:require(n),module:mod,exports:mod.exports,console,Buffer,setTimeout,clearTimeout},{filename:file});
const {P2LClient,WaveDispatcher,BatchDispatcher,BatchDispatch,CommandError}=mod.exports;
function client(){const c=new P2LClient('unused.invalid',1883);c.sent=[];c._client={connected:true,publish:(topic,payload,opts,cb)=>{c.sent.push({topic,payload,opts});cb?.(null);}};return c;}
function sent(c,index=-1){return JSON.parse(c.sent.at(index).payload);}
function event(c,data,retain=false){c._dispatch('p2l/events',Buffer.from(JSON.stringify(data)),retain);}
function applied(c,cmd){event(c,{type:'command_result',commandId:cmd.commandId,correlationId:cmd.correlationId,status:'applied'});}
function confirm(cmd,item=cmd,eventId='a'){return {type:'confirmed',eventId,address:'0001',status:'Normal',correlationId:cmd.correlationId,commandItemId:item.commandItemId};}
test('commands preserve IDs; broker acceptance alone cannot resolve; exact result and exact-byte retry',async()=>{
 const c=client();const h=c.lightOn('0001',{correlationId:'s',commandItemId:'i',pickingDetailId:'d',blockNo:'B'});const cmd=sent(c);assert.ok(cmd.commandId);assert.ok(cmd.expiresAt);assert.equal(cmd.pickingDetailId,'d');assert.equal(cmd.blockNo,'B');
 await assert.rejects(h.wait(2),/unknown/);h.retry();assert.equal(c.sent[0].payload,c.sent[1].payload);
 event(c,{type:'command_result',commandId:h.commandId,correlationId:'other',status:'applied'});assert.equal(h._result,null);
 applied(c,cmd);assert.equal((await h).status,'applied');h.retry();applied(c,cmd);assert.equal((await h.wait(20)).status,'applied');
});
test('ignored handles have no rejected Promise; explicit rejected/uncertain wait throws typed outcome',async()=>{
 const c=client();for(const status of ['rejected','uncertain']){const h=c.lightOff('0001');event(c,{type:'command_result',commandId:h.commandId,status});await assert.rejects(h.wait(20),err=>err instanceof CommandError&&err.result.status===status);}
 c.terminateAll();event(c,{type:'command_result',commandId:sent(c).commandId,status:'rejected'});await new Promise(r=>setImmediate(r));
});
test('wave cannot consume B by duplicate/old item A, including different event IDs and retained copy',async()=>{
 const c=client(),w=new WaveDispatcher(c,[{sku:'A',quantity:1},{sku:'B',quantity:2}],{positions:['0001']});w.start();const a=sent(c);applied(c,a);event(c,confirm(a));const b=sent(c);applied(c,b);
 event(c,confirm(a));event(c,confirm(a,a,'different-id'));event(c,confirm(b,b,'retained'),true);assert.equal(w._confirmed,1);assert.equal(w._active['0001'].sku,'B');event(c,confirm(b,b,'b'));assert.equal((await w.wait(20)).confirmed,2);
});
test('batch assigns item IDs and checks exact identity for every following lightOn',async()=>{
 const c=client();c._states.set('0001',{address:'0001',label:'POS'});const batch=new BatchDispatch('batch',[{sku:'A',label:'POS',quantity:1},{sku:'B',label:'POS',quantity:2}]);const d=new BatchDispatcher(c,batch);d.start();const a=sent(c);assert.ok(a.items[0].commandItemId);applied(c,a);event(c,confirm(a,a.items[0]));const b=sent(c);assert.equal(b.correlationId,a.correlationId);assert.ok(b.commandItemId);applied(c,b);event(c,confirm(a,a.items[0],'different'));assert.equal(d._confirmed,1);event(c,confirm(b,b,'b'));assert.equal((await d.wait(20)).confirmed,2);
});
test('dispatch wait reports application rejection without advancing item',async()=>{
 const c=client(),w=new WaveDispatcher(c,[{sku:'A',quantity:1}],{positions:['0001']});w.start();const a=sent(c);event(c,{type:'command_result',commandId:a.commandId,correlationId:a.correlationId,status:'rejected',errorCode:'ADDRESS_BUSY'});await assert.rejects(w.wait(50),/rejected/);assert.equal(w._confirmed,0);
});
test('public batch fields are preserved and command-result replay with same eventId still resolves retry',async()=>{
 const c=client();const h=c.batchStart([{address:'0001',quantity:1,commandItemId:'i',pickingDetailId:'d'}],{correlationId:'s',blockNo:'B',relight:true});const cmd=sent(c);assert.equal(cmd.items[0].pickingDetailId,'d');assert.equal(cmd.blockNo,'B');assert.equal(cmd.relight,true);const outcome={type:'command_result',eventId:'same',commandId:h.commandId,correlationId:'s',status:'applied'};event(c,outcome);await h;h.retry();event(c,outcome);await h.wait(20);
});

test('connect waits for usable subscription and keeps reconnect errors handled',async()=>{
 const {EventEmitter}=require('node:events');let subscribe;
 const transport=new EventEmitter();transport.connected=true;transport.end=()=>{};
 transport.subscribe=(topics,options,callback)=>{subscribe=callback;};
 mqttStub.connect=()=>transport;const c=new P2LClient('unused.invalid',1883);
 let ready=false;const connecting=c.connect(100).then(()=>{ready=true;});transport.emit('connect');
 await Promise.resolve();assert.equal(ready,false);subscribe(null,[{qos:1}]);await connecting;
 assert.doesNotThrow(()=>{transport.emit('error',new Error('first reconnect error'));transport.emit('error',new Error('second reconnect error'));});
 const denied=new EventEmitter();denied.connected=true;denied.end=()=>{};denied.subscribe=(topics,options,cb)=>cb(null,[{qos:128}]);
 mqttStub.connect=()=>denied;const bad=new P2LClient('unused.invalid',1883);const failure=bad.connect(100);denied.emit('connect');await assert.rejects(failure,/subscription/);
});

test('an old retry handle cannot overwrite another pending intent with the same ID',async()=>{
 const c=client();const a=c.lightOn('0001',{commandId:'same',expiresAt:'2030-01-01T00:00:00Z'});applied(c,sent(c));await a;
 const b=c.lightOn('0001',{commandId:'same',expiresAt:'2030-01-01T00:00:00Z',quantity:2});
 assert.throws(()=>a.retry(),/another pending handle/);assert.equal(c._pendingCommands.get('same'),b);
 event(c,{type:'command_result',commandId:'same',status:'rejected',errorCode:'COMMAND_ID_CONFLICT'});await assert.rejects(b.wait(20),/rejected/);
});
