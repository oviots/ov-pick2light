'use strict';
// Explicit UAT fixture reset. Deletes only P2L_TEST business data, preserving block config and generators.
const fs = require('node:fs');
const crypto = require('node:crypto');
const {MongoClient, ObjectId} = require('mongoose').mongo;
const fixture = require('./fixture.json');
const database = 'sft_uat_p2l_local_3lights_20261113';
const batch = 'P2L-DEACTIVE-WEB-20261113-01';
const scope = {ClientCode:'MML',WarehouseCode:'MIENNAM',WarehouseSiteId:'1335'};
const action = process.argv[2] || 'plan';
if (!['plan','apply'].includes(action)) throw Error('Use plan or apply');
const uri = JSON.parse(fs.readFileSync('/Volumes/GTeck/Projects/1shot/workspaces/mml/rocket-wms-service/server/modules/default/conf.json','utf8')).MONGO_URI;
const endpoint = new URL(uri);
if (endpoint.hostname !== '192.168.12.6' || endpoint.port !== '37017') throw Error('Expected UAT endpoint');
const client = new MongoClient(uri,{serverSelectionTimeoutMS:5000});
const id = label => new ObjectId(crypto.createHash('sha256').update(`${batch}:${label}`).digest('hex').slice(0,24));
const demands = [
 {block:'01',store:'TEST_WEB_01_A',lines:[['0008',3],['0014',2]]},
 {block:'02',store:'TEST_WEB_02_A',lines:[['0017',2]]},
 {block:'01',store:'TEST_WEB_01_B',lines:[['0008',1],['0014',4]]},
 {block:'02',store:'TEST_WEB_02_B',lines:[['0017',3]]},
];
async function main() {
 await client.connect();const db = client.db(database);
 if (!await db.collection('P2L.TestFixture').findOne({_id:fixture.fixture})) throw Error('Original fixture missing');
 const names = (await db.listCollections({},{nameOnly:true}).toArray()).map(x=>x.name).filter(n=>n.startsWith('P2L.') && n!=='P2L.Blocks' && n!=='P2L.TestFixture');
 const codes = (await db.collection('P2L.PickingSessions').find({ClientCode:'P2L_TEST'},{projection:{Code:1}}).toArray()).map(x=>x.Code);
 const filters = new Map(names.map(name=>[name,name==='P2L.Signals'
  ? {$or:[{ClientCode:'P2L_TEST'},{'Data.clientCode':'P2L_TEST'},{SessionCode:{$in:codes}}]}
  : {ClientCode:'P2L_TEST'}]));
 const counts = {};
 for(const [name,filter] of filters) counts[name] = await db.collection(name).countDocuments(filter);
 console.log(JSON.stringify({database,action,newBatch:batch,scope,oldClientRows:counts}));
 if(action!=='apply')return;
 const session = client.startSession();
 try {
  await session.withTransaction(async()=>{
   if(await db.collection('P2L.TestFixture').findOne({_id:batch},{session})) throw Error('Web fixture already prepared; refusing to reset again');
   const stockLabels=fixture.blocks.flatMap(b=>b.lamps.map(l=>l.bin));
   if(await db.collection('P2L.Storages').countDocuments({...scope,LocationLabel:{$in:stockLabels}},{session}))throw Error('Target stock already exists');
   const currentCodes=(await db.collection('P2L.PickingSessions').find({ClientCode:'P2L_TEST'},{session,projection:{Code:1}}).toArray()).map(row=>row.Code);
   filters.set('P2L.Signals',{$or:[{ClientCode:'P2L_TEST'},{'Data.clientCode':'P2L_TEST'},{SessionCode:{$in:currentCodes}}]});
   for(const [name,filter] of filters) await db.collection(name).deleteMany(filter,{session});
   const now=new Date();
   const audit={IsDeleted:0,CreatedBy:'P2L_WEB_SCOPE_TEST',UpdatedBy:'P2L_WEB_SCOPE_TEST',CreatedDate:now,UpdatedDate:now};
   const devices=[],stocks=[],splits=[],details=[];
   for(const block of fixture.blocks) {
    if(!await db.collection('P2L.Blocks').findOne({Code:block.code,IsDeleted:0},{session}))throw Error('Block config missing');
    for(const lamp of block.lamps) {
     devices.push({_id:id(`bin:${lamp.address}`),...scope,Code:lamp.bin,Name:lamp.bin,Type:'BIN',LEDAddress:lamp.address,CurrentZone:block.zone,Status:'Available',...audit});
     stocks.push({_id:id(`stock:${lamp.address}`),...scope,LocationLabel:lamp.bin,LocationType:'BIN',SKU:lamp.sku,SKUName:lamp.sku,Uom:'EA',Qty:100,PendingOutQty:0,...audit});
    }
   }
   for(const demand of demands) {
    const splitId=id(demand.store);
    splits.push({_id:splitId,...scope,RocketCode:batch,BlockCode:demand.block,StoreCode:demand.store,StoreName:demand.store,StoreRoute:demand.store.endsWith('_A')?'01':'02',IsPriority:false,TotalItems:demand.lines.length,TotalQty:demand.lines.reduce((n,l)=>n+l[1],0),PickedQty:0,IsPickingCompleted:false,Status:'Processing',ManualPicking:false,P2LMode:{Color:demand.block==='01'?'Green':'Red',Led:'Flash',Buz:'Off'},...audit});
    for(const [address,qty] of demand.lines) {
     const lamp=fixture.blocks.flatMap(b=>b.lamps).find(l=>l.address===address);
     details.push({_id:id(`${demand.store}:${address}`),...scope,RefId:String(splitId),RocketCode:batch,BlockCode:demand.block,SKU:lamp.sku,Name:lamp.sku,Uom:'EA',Qty:qty,PickedQty:0,Status:'Pending',...audit});
    }
   }
   await db.collection('P2L.TransportDevices').insertMany(devices,{session});
   await db.collection('P2L.Storages').insertMany(stocks,{session});
   await db.collection('P2L.DetailSplits').insertMany(details,{session});
   await db.collection('P2L.DemandSplits').insertMany(splits,{session});
   await db.collection('P2L.TestFixture').insertOne({_id:batch,createdAt:now,scope,demands,initialStock:stocks.map(({LocationLabel,Qty,PendingOutQty})=>({LocationLabel,Qty,PendingOutQty})),expectedSessions:4,expectedDetails:6,expectedPickedQty:15,ledMode:'Flash'},{session});
   await db.collection('P2L.TestFixture').updateOne({_id:fixture.fixture},{$set:{scope,scopeUpdatedAt:now}},{session});
  });
 } finally {await session.endSession();}
 console.log(JSON.stringify({prepared:true,scope,batch,demands:4,details:6,stocks:3,devices:3,blocksAndCounters:'unchanged'}));
}
main().catch(error=>{console.error(error.name,error.message?.replace(uri,'[Mongo URI]'));process.exitCode=1;}).finally(()=>client.close());
