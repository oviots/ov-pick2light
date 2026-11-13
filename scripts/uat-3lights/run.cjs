'use strict';
// This harness uses real WMS modules and UAT Mongo/MQTT. Only label printing is stubbed.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {createRequire} = require('node:module');
process.env.NODE_PATH = [path.join(__dirname,'node_modules'),process.env.NODE_PATH].filter(Boolean).join(path.delimiter);
require('node:module').Module._initPaths();
const {spawnSync} = require('node:child_process');
const fixture = require('./fixture.json');
const lightRoot = path.resolve(__dirname, '../..');
const wmsRoot = process.env.P2L_TEST_WMS_REPO || '/Volumes/GTeck/Projects/1shot/workspaces/mml/rocket-wms-service';
const wmsRequire = createRequire(path.join(wmsRoot, 'package.json'));
const {MongoClient, ObjectId} = wmsRequire('mongoose').mongo;
const action = process.argv[2] || 'check';
const configPath = path.join(wmsRoot, 'server/modules/default/conf.json');
const sourceUri = JSON.parse(fs.readFileSync(configPath, 'utf8')).MONGO_URI;
const endpoint = new URL(sourceUri);
if (endpoint.hostname !== '192.168.12.6' || endpoint.port !== '37017') throw Error('Expected UAT Mongo 192.168.12.6:37017');
if (!/^sft_uat_p2l_local_3lights_\d{8}$/.test(fixture.database)) throw Error('Unexpected test database');
const client = new MongoClient(sourceUri, {serverSelectionTimeoutMS:5000,connectTimeoutMS:5000});
const scope = fixture.scope;
const id = label => new ObjectId(crypto.createHash('sha256').update(`${fixture.fixture}:${label}`).digest('hex').slice(0,24));
const audit = () => ({IsDeleted:0,CreatedBy:'P2L_LOCAL_TEST',CreatedDate:new Date(),UpdatedBy:'P2L_LOCAL_TEST',UpdatedDate:new Date()});
const names = ['SFT.ScheduleManagements','P2L.Blocks','P2L.TransportDevices','P2L.Storages','P2L.DemandSplits','P2L.DetailSplits','P2L.PickingSessions','P2L.PickingDetails','P2L.Signals','P2L.Transactions','P2L.ScannedItems','P2L.PickingAssignees','__generators'];
function rows() {
  const docs = Object.fromEntries(names.map(name => [name,[]]));
  docs['SFT.ScheduleManagements'].push({_id:id('schedule'),Name:'PICK_2_LIGHT_SCHEDULE',Status:'ENABLED',ScheduleInterval:5,NumberOfThead:1,...audit()});
  for (const block of fixture.blocks) {
    docs['P2L.Blocks'].push({_id:id(`block:${block.code}`),Code:block.code,Name:`LOCAL ${block.code}`,Status:'Active',MQTTConfig:{},PickingConfig:{zoneCode:block.zone,dispatchIntervalMs:5000,maxConcurrentSessions:1},SignalConfig:{autoTrigger:true,triggerIntervalMs:1000},...audit()});
    for (const lamp of block.lamps) {
      docs['P2L.TransportDevices'].push({_id:id(`bin:${lamp.address}`),...scope,Code:lamp.bin,Name:lamp.bin,Type:'BIN',LEDAddress:lamp.address,CurrentZone:block.zone,Status:'Available',...audit()});
      docs['P2L.Storages'].push({_id:id(`stock:${lamp.address}`),...scope,LocationLabel:lamp.bin,LocationType:'BIN',SKU:lamp.sku,SKUName:lamp.sku,Uom:'EA',Qty:lamp.stock,PendingOutQty:0,...audit()});
    }
  }
  for (const demand of fixture.demands) {
    const splitId = id(`split:${demand.store}`);
    docs['P2L.DemandSplits'].push({_id:splitId,...scope,RocketCode:fixture.rocketCode,BlockCode:demand.block,StoreCode:demand.store,StoreName:demand.store,StoreRoute:demand.route,IsPriority:false,TotalItems:demand.lines.length,TotalQty:demand.lines.reduce((n,x)=>n+x.qty,0),PickedQty:0,IsPickingCompleted:false,Status:'Processing',P2LMode:{Color:demand.block==='01'?'Green':'Red',Led:'On',Buz:'Off'},ManualPicking:false,...audit()});
    for (const line of demand.lines) {
      const lamp = fixture.blocks.find(b=>b.code===demand.block).lamps.find(l=>l.address===line.address);
      docs['P2L.DetailSplits'].push({_id:id(`line:${demand.store}:${line.address}`),...scope,RefId:String(splitId),BlockCode:demand.block,RocketCode:fixture.rocketCode,SKU:lamp.sku,Name:lamp.sku,Uom:'EA',Qty:line.qty,PickedQty:0,Status:'Pending',...audit()});
    }
  }
  return docs;
}
async function inspect(db) {
  const collections = await db.listCollections({}, {nameOnly:true}).toArray();
  const counts = {};
  for (const {name} of collections) if (names.includes(name)) counts[name]=await db.collection(name).countDocuments({});
  console.log(JSON.stringify({host:endpoint.hostname,port:Number(endpoint.port),database:fixture.database,counts},null,2));
}
async function seed(db) {
  const marker = await db.collection('P2L.TestFixture').findOne({_id:fixture.fixture});
  if (marker) {await configure(db);console.log('Fixture already exists; inventory/demand unchanged. Schedule/MQTT config ready.');return inspect(db);}
  if ((await db.listCollections({}, {nameOnly:true}).toArray()).length) throw Error('Refusing to seed a non-empty database');
  // No database drop, delete, or updates to other DBs.
  const docs=rows();
  for (const [name,values] of Object.entries(docs)) {
    await db.createCollection(name);
    if (values.length) await db.collection(name).insertMany(values);
  }
  await db.collection('P2L.Signals').createIndex({Key:1},{unique:true});
  await db.collection('P2L.Signals').createIndex({SessionCode:1,Name:1,IsDeleted:1});
  await db.collection('P2L.PickingSessions').createIndex({Code:1},{unique:true});
  await db.collection('P2L.PickingSessions').createIndex({Status:1,IsDeleted:1,BlockCode:1});
  await db.collection('P2L.PickingDetails').createIndex({SessionCode:1,IsDeleted:1});
  await db.collection('P2L.Transactions').createIndex({IdempotencyKey:1},{unique:true,partialFilterExpression:{IdempotencyKey:{$type:'string'}}});
  await db.collection('P2L.DetailSplits').createIndex({RefId:1,IsDeleted:1});
  await db.collection('P2L.TestFixture').insertOne({_id:fixture.fixture,createdAt:new Date(),fixture});
  await configure(db);
  await inspect(db);
}
function licensedConfig() {
  const result = spawnSync(path.join(lightRoot,'.venv/bin/python'),['-c',`import sys,json;sys.path.insert(0,${JSON.stringify(path.join(lightRoot,'client'))});from app.licensing.provider import fetch;print(json.dumps(fetch()))`], {encoding:'utf8',env:{...process.env,P2L_LICENSE_API_URL:process.env.P2L_LICENSE_API_URL||'https://auth-supra.oviots.com'},timeout:45000});
  if (result.status!==0) throw Error('Cannot load licensed MQTT client config. Check activation/API (credentials are not printed).');
  const config=JSON.parse(result.stdout);
  if (config.gateway.host!==fixture.gateway.host) throw Error('License points at a different controller');
  if (!fixture.gateway.blockNos.every(b=>(config.gateway.blockNos||[config.gateway.blockNo]).includes(b))) throw Error('License must allow blockNos 01 and 02');
  if (config.mqtt.commandTopic!=='p2l/uat/test01/commands') throw Error('Unexpected MQTT command topic; review test isolation');
  return config;
}
async function configure(db) {
  const remote=licensedConfig(), mqtt=remote.mqtt;
  const prefix=mqtt.commandTopic.replace(/\/commands$/,'');
  if (mqtt.eventTopic!==`${prefix}/events` || mqtt.statusTopic!==`${prefix}/status`) throw Error('Unexpected shared MQTT topic prefix');
  const tls=typeof mqtt.tls==='object'?mqtt.tls:{rejectUnauthorized:true};
  // WMS reads these from P2L.Blocks. Credentials remain only in UAT Mongo, never fixture/output.
  for(const block of fixture.blocks) await db.collection('P2L.Blocks').updateOne({Code:block.code,IsDeleted:0},{$set:{MQTTConfig:{broker:mqtt.host,port:mqtt.port,protocol:'mqtts',clientId:`wms-uat-3lights-${block.code}`,username:mqtt.username,password:mqtt.password,prefixTopic:prefix,tls,subscribeTopics:[mqtt.eventTopic,mqtt.devicesTopic,`${mqtt.devicesTopic}/#`,mqtt.statusTopic]}}});
  await db.collection('SFT.ScheduleManagements').updateOne({Name:'PICK_2_LIGHT_SCHEDULE'},{$setOnInsert:{_id:id('schedule'),Name:'PICK_2_LIGHT_SCHEDULE',Status:'ENABLED',ScheduleInterval:5,NumberOfThead:1,...audit()}},{upsert:true});
}
async function loadWms() {
  endpoint.pathname='/'+fixture.database;
  process.env.MONGO_URI=endpoint.toString();
  // Only the dedicated test DB is available to the WMS adapter.
  delete process.env.DB_MAPS;
  process.env.APP_DIRECTORY=path.join(wmsRoot,'server');
  process.env.TZ='Asia/Ho_Chi_Minh';
  process.env.P2L_ACTIVE_MODULE='pick2light';
  process.env.SIGTERM_SIGNAL_TIMEOUT='10000';
  process.env.APP_INSTANCE='local-3lights';
  process.env.OV_SMART_PRINT_URL='http://p2l-test-printer.invalid';
  global.isEmpty=v=>v==null||v===''||(Array.isArray(v)&&!v.length)||(typeof v==='object'&&!Object.keys(v).length);
  const utils=wmsRequire.resolve('./server/modules/default/lib/utils.js');
  require.cache[utils]={id:utils,filename:utils,loaded:true,exports:{generateCode:n=>crypto.randomBytes(n).toString('hex').slice(0,n),sleep:ms=>new Promise(r=>setTimeout(r,ms))}};
  const fetchOriginal=global.fetch;
  global.fetch=async (url,options)=> {
    if (String(url)==='http://p2l-test-printer.invalid/cloud-print/v1/print') {console.log('[TEST] Label print skipped');return {ok:true,status:200};}
    return fetchOriginal(url,options);
  };
  const {MongoAdapter}=wmsRequire('./server/core/database');
  global.DB=new MongoAdapter();
  DB.resolveModels(['pick2light']);
  await DB.getClient().asPromise();
  DB.getCollection('ScheduleManagement',wmsRequire('./server/modules/default/model/process/sftScheduleManagements.js'));
  for (const name of ['P2LBlockConfigs','P2LDemandSplits','P2LDetailSplits','P2LStorages','P2LTransportDevices','P2LPickingSessions','P2LPickingDetails','P2LSignals','P2LTransactions','P2LScannedItems','P2LPickingAssignees','Generator']) DB.getCollection(name);
}
async function run(db) {
  if (!await db.collection('P2L.TestFixture').findOne({_id:fixture.fixture})) throw Error('Seed the test database first');
  licensedConfig(); // Check that this machine is still enrolled for the test gateway/topics.
  delete process.env.P2L_BLOCK_CONFIGS; // Exercise the normal DB configuration path.
  await loadWms();
  const schedule=wmsRequire('./server/modules/pick2light/schedule');
  const shutdown=wmsRequire('./server/core/utils/gracefulShutdown');
  let stopping=false;
  const stop=async()=>{if(stopping)return;stopping=true;await shutdown.onSignal();await DB.getClient().close();await client.close();process.exit(0);};
  process.once('SIGINT',stop);process.once('SIGTERM',stop);
  schedule.start();
  console.log(`WMS schedule starting in ${fixture.database} (6s startup delay). Ctrl+C preserves pending work.`);
}
async function preflight(db) {
  await loadWms();
  try {
    // Compile the real runtime modules, but do not initialize MQTT or start timers.
    wmsRequire('./server/modules/pick2light/schedule');
    const schedule=await DB.getCollection('ScheduleManagement').findOne({Name:'PICK_2_LIGHT_SCHEDULE',Status:'ENABLED',IsDeleted:0});
    if(!schedule) throw Error('Missing enabled PICK_2_LIGHT_SCHEDULE');
    const blocks=await DB.getCollection('P2LBlockConfigs').find({IsDeleted:0});
    const demands=await DB.getCollection('P2LDemandSplits').find({Status:{$in:['Processing']},IsDeleted:0});
    const details=await DB.getCollection('P2LDetailSplits').find({RefId:{$in:demands.map(d=>String(d._id))},IsDeleted:0});
    const devices=await DB.getCollection('P2LTransportDevices').find({Type:'BIN',IsDeleted:0});
    if(blocks.some(b=>!b.MQTTConfig?.broker||!b.MQTTConfig?.password)) throw Error('Missing WMS MQTT config');
    if(blocks.length!==2||demands.length!==4||details.length!==6||devices.length!==3) throw Error('Fixture relation/count mismatch');
    if(await db.collection('P2L.PickingSessions').countDocuments({})!==0) throw Error('Test already has sessions; use status to inspect');
    console.log('Preflight OK: real WMS models/modules loaded; 2 blocks / 4 demands / 6 lines / 3 mapped lamps. No MQTT connection, no light commands.');
  } finally { await DB.getClient().close(); }
}
async function status(db) {
  await inspect(db);
  console.log('Sessions:',JSON.stringify(await db.collection('P2L.PickingSessions').find({},{projection:{_id:0,Code:1,BlockCode:1,StoreCode:1,Status:1,TotalQty:1,PickedQty:1}}).toArray(),null,2));
  console.log('Stock:',JSON.stringify(await db.collection('P2L.Storages').find({LocationType:'BIN'},{projection:{_id:0,LocationLabel:1,Qty:1,PendingOutQty:1}}).toArray(),null,2));
}
(async()=>{await client.connect();const hello=await client.db('admin').command({hello:1});if(!hello.setName)throw Error('Replica set required for WMS transactions');const db=client.db(fixture.database);if(action==='seed')await seed(db);else if(action==='run')return await run(db);else if(action==='status')await status(db);else if(action==='preflight')await preflight(db);else if(action==='check')await inspect(db);else throw Error('Use check, seed, status, preflight or run');await client.close();})().catch(async error=>{console.error('P2L test:',error.code||error.name,error.message?.replace(sourceUri,'[Mongo URI]'));await client.close().catch(()=>{});process.exit(1);});
