// Registers ADM in the licence dashboard and creates a licence key for it.
//
// The licence service on metariqul.vercel.app stores one row per shipped
// application. An application is identified by its own API key, which travels
// in the x-app-key header, and a licence key only works for the application
// it was issued for. That means a new build of ADM needs one row in the
// dashboard before any customer can activate it.
//
//   node tools\Register-LicenseApp.mjs --dashboard "D:\path\to\portfolio"
//
// The dashboard path is the folder of the Next.js project that serves
// /api/license/*. It needs its node_modules and its .env, because the row is
// written straight into the database.
//
// The script is idempotent: running it again reports the existing application
// and licence instead of creating a second copy. It never deletes anything.
//
// Options:
//   --dashboard <path>   the licence dashboard project (required)
//   --name <text>        the display name of the application
//   --slug <text>        the short name; it becomes the licence key prefix
//   --devices <n>        how many devices one licence may activate (default 1)
//   --label <text>       a note on the licence, for example the customer
//   --rotate             issue a new API key and keep the old one for a month
//
import { randomBytes } from "node:crypto";
import { createRequire } from "node:module";
import { existsSync } from "node:fs";
import { join } from "node:path";

function arg(name, fallback) {
  const index = process.argv.indexOf(name);
  return index >= 0 && index + 1 < process.argv.length ? process.argv[index + 1] : fallback;
}

const dashboard = arg("--dashboard");
if (!dashboard) {
  console.error("Pass --dashboard with the path of the licence dashboard project.");
  process.exit(1);
}

const name = arg("--name", "ADM All Download Manager");
const slug = arg("--slug", "adm");
const maxDevices = Number.parseInt(arg("--devices", "1"), 10) || 1;
const label = arg("--label", "Developer copy");
const rotate = process.argv.includes("--rotate");

if (!existsSync(join(dashboard, "node_modules", "@prisma", "client"))) {
  console.error(`No Prisma client was found in ${dashboard}. Run npm install there first.`);
  process.exit(1);
}

if (existsSync(join(dashboard, ".env"))) {
  process.loadEnvFile(join(dashboard, ".env"));
}

// The dashboard keeps its own dependency tree, so the client is loaded from
// there rather than from this repository.
const require = createRequire(join(dashboard, "package.json"));
const { PrismaClient } = require("@prisma/client");

const prisma = new PrismaClient();

const newApiKey = () => `appk_${randomBytes(24).toString("base64url")}`;

const randomBlock = () =>
  randomBytes(2).toString("hex").toUpperCase().replace(/[^A-Z0-9]/g, "X").slice(0, 4);

const formatLicenseKey = (value) => {
  const prefix = value.replace(/[^a-z0-9]/gi, "").slice(0, 5).toUpperCase() || "LIC";
  return [prefix, randomBlock(), randomBlock(), randomBlock()].join("-");
};

async function uniqueLicenseKey(appId) {
  for (let attempt = 0; attempt < 8; attempt++) {
    const key = formatLicenseKey(slug);
    const taken = await prisma.license.findUnique({ where: { key }, select: { id: true } });
    if (!taken) return key;
  }
  throw new Error("Could not allocate a licence key.");
}

async function main() {
  // An application that is already registered is reused, whatever its slug
  // is, so a second run never creates a duplicate.
  let app = await prisma.app.findUnique({ where: { slug } });
  if (!app) {
    app = await prisma.app.findFirst({ where: { name }, orderBy: { createdAt: "asc" } });
  }

  if (!app) {
    app = await prisma.app.create({
      data: {
        name,
        slug,
        apiKey: newApiKey(),
        description: "The ADM All Download Manager desktop app.",
        requestMode: "AUTO",
        recheckDays: 7,
      },
    });
    console.log(`created the application ${app.name} (${app.slug})`);
  } else {
    console.log(`the application ${app.name} (${app.slug}) already exists`);
  }

  let apiKey = app.apiKey;

  if (rotate) {
    // The dashboard keeps the old key for a month, so a rotation does not
    // break the copies that are already installed.
    const previous = app.apiKey;
    apiKey = newApiKey();
    app = await prisma.app.update({
      where: { id: app.id },
      data: {
        apiKey,
        previousApiKey: previous,
        previousApiKeyExpiresAt: new Date(Date.now() + 30 * 86_400_000),
      },
    });
    console.log(`rotated the API key; the old one stays valid for 30 days`);
  }

  let license = await prisma.license.findFirst({
    where: { appId: app.id, label },
  });

  if (!license) {
    license = await prisma.license.create({
      data: {
        appId: app.id,
        key: await uniqueLicenseKey(app.id),
        label,
        maxDevices,
        status: "ACTIVE",
      },
    });
    console.log(`created a licence for ${maxDevices} device(s)`);
  } else {
    console.log(`a licence for "${label}" already exists`);
  }

  console.log("");
  console.log("  app key       :", apiKey);
  console.log("  licence key   :", license.key);
  console.log("  app slug      :", app.slug);
  console.log("  recheck days  :", app.recheckDays);
  console.log("");
  console.log("Put the app key in src/ADM.Core/Licensing/LicenseConfiguration.cs and");
  console.log("give the licence key to the person who installs the build.");
}

main()
  .catch((error) => {
    console.error("The registration failed:", error);
    process.exitCode = 1;
  })
  .finally(() => prisma.$disconnect());
