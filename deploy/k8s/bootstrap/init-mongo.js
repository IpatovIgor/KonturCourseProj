const replicaHost = 'mongo-0.mongo-peer.habit-tracker.svc.cluster.local:27017';
const adminPassword = encodeURIComponent(process.env.MONGO_ADMIN_PASSWORD);
const adminUri = 'mongodb://habit-admin:' + adminPassword + '@' + replicaHost
    + '/admin?directConnection=true&serverSelectionTimeoutMS=3000';

function waitForMongo(check, errorMessage) {
    const deadline = Date.now() + 180_000;
    while (Date.now() < deadline) {
        if (check()) return;
        sleep(2_000);
    }
    throw new Error(errorMessage);
}

let admin;
waitForMongo(() => {
    try {
        const connection = new Mongo(adminUri);
        admin = connection.getDB('admin');
        return admin.runCommand({ ping: 1 }).ok === 1;
    } catch (error) {
        if (error.code === 18) throw new Error('MongoDB rejected the bootstrap credentials.');
        return false;
    }
}, 'MongoDB did not become reachable.');

let replicaConfig;
try {
    replicaConfig = admin.runCommand({ replSetGetConfig: 1 }).config;
} catch (error) {
    if (error.code !== 94) throw error;
    admin.runCommand({ replSetInitiate: { _id: 'rs0', members: [{ _id: 0, host: replicaHost }] } });
}

if (replicaConfig && (replicaConfig._id !== 'rs0' || replicaConfig.members.length !== 1
    || replicaConfig.members[0].host !== replicaHost)) {
    throw new Error('Existing replica set configuration does not match this deployment.');
}

waitForMongo(() => admin.runCommand({ hello: 1 }).isWritablePrimary,
    'MongoDB did not elect a writable primary.');

const accounts = [
    { name: 'webapi', database: 'habit_tracker_auth', password: process.env.MONGO_WEBAPI_PASSWORD },
    { name: 'worker', database: 'habit_tracker_worker', password: process.env.MONGO_WORKER_PASSWORD },
    { name: 'notifications', database: 'habit_tracker_notifications', password: process.env.MONGO_NOTIFICATIONS_PASSWORD },
];

for (const account of accounts) {
    if (!account.password) throw new Error('Missing MongoDB password for ' + account.name);
    const database = admin.getSiblingDB(account.database);
    const credentials = { pwd: account.password, roles: [{ role: 'readWrite', db: account.database }] };
    if (database.getUser(account.name)) {
        database.updateUser(account.name, credentials);
    } else {
        database.createUser({ user: account.name, ...credentials });
    }
}

print('MongoDB replica set and application accounts are ready.');
