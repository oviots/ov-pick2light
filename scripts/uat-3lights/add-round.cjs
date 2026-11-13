'use strict';
// Append-only test demands. Does not start WMS, publish MQTT, reset stock or create sessions.
const fs = require('node:fs');
const crypto = require('node:crypto');
const {MongoClient, ObjectId} = require('mongoose').mongo;
const fixture = require('./fixture.json');
const database = 'sft_uat_p2l_local_3lights_20261113';
const round = process.argv[3] || 'r15';
const rounds = {
    r15: {batch:'P2L-API-20261113-R15-01',prefix:'TEST_R15_',led:'On',status:'Processing'},
    flash: {batch:'P2L-FLASH-20261113-01',prefix:'TEST_FLASH_',led:'Flash',status:'Processing'},
    relight: {batch:'P2L-RELIGHT-20261113-01',prefix:'TEST_RELIGHT_',led:'Flash',status:'Processing'},
    api: {batch:'P2L-HTTP-20261113-01',prefix:'TEST_HTTP_',led:'Flash',status:'Paused'},
    latency: {batch:'P2L-LATENCY-20261113-01',prefix:'TEST_LATENCY_',led:'Flash',status:'Processing'},
    priority: {batch:'P2L-LATENCY-20261113-02',prefix:'TEST_PRIORITY_',led:'Flash',status:'Processing'},
    priority2: {batch:'P2L-LATENCY-20261113-03',prefix:'TEST_PRIORITY2_',led:'Flash',status:'Processing'},
    deactive: {batch:'P2L-DEACTIVE-20261113-01',prefix:'TEST_DEACTIVE_',led:'Flash',status:'Processing'},
};
const setup = rounds[round];
if (!setup) throw Error('Round must be r15, flash, relight, api, latency, priority, priority2 or deactive');
const batch = setup.batch, ledMode = setup.led;
const action = process.argv[2] || 'plan';
if (!['plan', 'seed', 'status', 'activate'].includes(action) || (action === 'activate' && round !== 'api'))
    throw Error('Use plan/seed/status ROUND, or activate api');
const configPath = '/Volumes/GTeck/Projects/1shot/workspaces/mml/rocket-wms-service/server/modules/default/conf.json';
const uri = JSON.parse(fs.readFileSync(configPath, 'utf8')).MONGO_URI;
const endpoint = new URL(uri);
if (endpoint.hostname !== '192.168.12.6' || endpoint.port !== '37017') throw Error('Expected UAT Mongo endpoint');
const client = new MongoClient(uri, {serverSelectionTimeoutMS:5000,connectTimeoutMS:5000});
const scope = fixture.scope;
const demands = [
    {block:'01', store:'TEST_R15_01_A', route:'01', lines:[{address:'0008',qty:3},{address:'0014',qty:2}]},
    {block:'02', store:'TEST_R15_02_A', route:'01', lines:[{address:'0017',qty:2}]},
    {block:'01', store:'TEST_R15_01_B', route:'02', lines:[{address:'0008',qty:1},{address:'0014',qty:4}]},
    {block:'02', store:'TEST_R15_02_B', route:'02', lines:[{address:'0017',qty:3}]},
];
if (round === 'relight') demands.splice(2);
for (const demand of demands) demand.store = demand.store.replace('TEST_R15_', setup.prefix);
const lamps = fixture.blocks.flatMap(b=>b.lamps);
const id = label => new ObjectId(crypto.createHash('sha256').update(`${batch}:${label}`).digest('hex').slice(0,24));
const audit = now => ({IsDeleted:0,CreatedBy:'P2L_R15_TEST',CreatedDate:now,UpdatedBy:'P2L_R15_TEST',UpdatedDate:now});
async function snapshot(db, session) {
    const options = session ? {session} : {};
    if (!await db.collection('P2L.TestFixture').findOne({_id:fixture.fixture}, options)) throw Error('Original fixture missing: refusing to create or seed another DB');
    const stocks = await db.collection('P2L.Storages').find({...scope,LocationType:'BIN',LocationLabel:{$in:lamps.map(l=>l.bin)},IsDeleted:0},
        {...options,projection:{_id:0,LocationLabel:1,SKU:1,Qty:1,PendingOutQty:1}}).toArray();
    const existing = await db.collection('P2L.TestFixture').findOne({_id:batch}, options);
    const sessions = await db.collection('P2L.PickingSessions').find({...scope,IsDeleted:0,Status:{$in:['New','InProgress','Confirmed']}},
        {...options,projection:{_id:0,Code:1,BlockCode:1,StoreCode:1,Status:1,RocketCode:1}}).limit(20).toArray();
    return {stocks,existing:existing ? {batch:existing._id,createdAt:existing.createdAt} : null,activeSessions:sessions};
}
function validateStock(state) {
    const required = new Map();
    for(const demand of demands)for(const line of demand.lines)required.set(line.address,(required.get(line.address)||0)+line.qty);
    for(const lamp of lamps) {
        const matches=state.stocks.filter(s=>s.LocationLabel===lamp.bin && s.SKU===lamp.sku);
        if(matches.length!==1)throw Error(`Expected exactly one BIN stock for lamp ${lamp.address}`);
        const stock=matches[0];
        if(!Number.isFinite(stock.Qty)||!Number.isFinite(stock.PendingOutQty)||stock.Qty-stock.PendingOutQty<required.get(lamp.address))throw Error(`Not enough free stock for lamp ${lamp.address}`);
    }
}
async function seed(db) {
    const session=client.startSession();
    try {
        await session.withTransaction(async()=>{
            const state=await snapshot(db,session);
            if(state.existing)return;
            validateStock(state);
            for(const block of fixture.blocks) {
                const record=await db.collection('P2L.Blocks').findOne({Code:block.code,Status:'Active',IsDeleted:0},{session});
                if(!record||record.PickingConfig?.zoneCode!==block.zone)throw Error(`Block ${block.code} must be Active with the original zone`);
                for(const lamp of block.lamps) {
                    if(!await db.collection('P2L.TransportDevices').findOne({...scope,Code:lamp.bin,LEDAddress:lamp.address,CurrentZone:block.zone,IsDeleted:0},{session}))throw Error(`Lamp mapping changed: ${lamp.address}`);
                }
            }
            const now=new Date(),splits=[],details=[];
            for(const demand of demands) {
                const splitId=id(`split:${demand.store}`);
                splits.push({_id:splitId,...scope,RocketCode:batch,BlockCode:demand.block,StoreCode:demand.store,StoreName:demand.store,
                    StoreRoute:demand.route,IsPriority:false,TotalItems:demand.lines.length,TotalQty:demand.lines.reduce((n,l)=>n+l.qty,0),
                    PickedQty:0,IsPickingCompleted:false,Status:setup.status,P2LMode:{Color:demand.block==='01'?'Green':'Red',Led:ledMode,Buz:'Off'},ManualPicking:false,...audit(now)});
                for(const line of demand.lines) {
                    const lamp=lamps.find(l=>l.address===line.address);
                    details.push({_id:id(`line:${demand.store}:${line.address}`),...scope,RefId:String(splitId),BlockCode:demand.block,RocketCode:batch,
                        SKU:lamp.sku,Name:lamp.sku,Uom:'EA',Qty:line.qty,PickedQty:0,Status:'Pending',...audit(now)});
                }
            }
            await db.collection('P2L.DetailSplits').insertMany(details,{session});
            await db.collection('P2L.DemandSplits').insertMany(splits,{session});
            await db.collection('P2L.TestFixture').insertOne({_id:batch,createdAt:now,description:`${ledMode} test, 3 lamps / 2 blocks; original SKU-to-lamp mapping`,demands,ledMode,
                initialStock:state.stocks,expectedPickedQty:demands.reduce((n,d)=>n+d.lines.reduce((q,l)=>q+l.qty,0),0),expectedSessions:demands.length,expectedDetails:details.length},{session});
        });
    } finally {await session.endSession();}
}
async function main() {
    await client.connect();const db=client.db(database);
    const before=await snapshot(db);
    if(action==='seed')await seed(db);
    if(action==='activate') {
        if(!before.existing)throw Error('Seed this API batch first');
        validateStock(before);
        await db.collection('P2L.DemandSplits').updateMany({...scope,RocketCode:batch,Status:'Paused'},{$set:{Status:'Processing',UpdatedBy:'P2L_API_TEST',UpdatedDate:new Date()}});
    }
    const state=['seed','activate'].includes(action)?await snapshot(db):before;
    const splits=await db.collection('P2L.DemandSplits').find({...scope,RocketCode:batch},{projection:{_id:0,StoreCode:1,BlockCode:1,Status:1,TotalQty:1,PickedQty:1,P2LMode:1}}).toArray();
    const details=await db.collection('P2L.DetailSplits').countDocuments({...scope,RocketCode:batch});
    const sessions=await db.collection('P2L.PickingSessions').find({...scope,RocketCode:batch},{projection:{_id:0,Code:1,BlockCode:1,StoreCode:1,Status:1}}).toArray();
    console.log(JSON.stringify({database,batch,action,ledMode,plan:demands,...state,seededDemands:splits,seededDetailCount:details,batchSessions:sessions},null,2));
}
main().catch(error=>{console.error('Append test round failed:',error.message?.replace(uri,'[Mongo URI]'));process.exitCode=1;}).finally(()=>client.close());
