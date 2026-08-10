const releaseDownload =
  "https://github.com/UnknownGod2011/openai-cursivis/releases/download/v0.1.0-beta.4/Cursivis-Setup-0.1.0-beta.4-x64.exe";

const features = [
  {
    number: "01",
    title: "Context Trigger",
    body: "Select text—or capture part of your screen—and Cursivis infers the most useful next step right beside your cursor.",
  },
  {
    number: "02",
    title: "Guided Mode",
    body: "Choose from a compact set of actions generated for the exact text, code, image, or interface in front of you.",
  },
  {
    number: "03",
    title: "Prompt Optimizer",
    body: "Turn a rough selected prompt into a focused, ready-to-use instruction with one shortcut. The result is copied automatically.",
  },
  {
    number: "04",
    title: "Live Mode",
    body: "Talk naturally through a low-latency OpenAI Realtime conversation with selected-context and explicit Windows tools.",
  },
];

export default function Home() {
  return (
    <main>
      <nav className="nav shell" aria-label="Primary navigation">
        <a className="brand" href="#top" aria-label="Cursivis home">
          <span className="brand-mark" aria-hidden="true">
            <span />
          </span>
          <span>Cursivis</span>
        </a>
        <div className="nav-links">
          <a href="#features">Features</a>
          <a href="#install">Install</a>
          <a
            href="https://github.com/UnknownGod2011/openai-cursivis"
            target="_blank"
            rel="noreferrer"
          >
            GitHub
          </a>
        </div>
        <a className="nav-download" href={releaseDownload}>
          Download
        </a>
      </nav>

      <section className="hero shell" id="top">
        <div className="hero-copy">
          <div className="eyebrow">
            <span className="status-dot" /> Windows beta
          </div>
          <h1>
            OpenAI, right where
            <span> your cursor is.</span>
          </h1>
          <p className="hero-lede">
            Cursivis understands what you select, chooses the useful next action,
            and brings the result back without breaking your flow.
          </p>
          <div className="hero-actions">
            <a className="button button-primary" href={releaseDownload}>
              <span className="windows-glyph" aria-hidden="true">
                <i />
                <i />
                <i />
                <i />
              </span>
              Download for Windows
              <span aria-hidden="true">↓</span>
            </a>
            <span className="button button-secondary" aria-disabled="true">
              macOS <small>Coming Soon</small>
            </span>
          </div>
          <p className="hero-note">Windows 10 version 2004 or later · Bring your own OpenAI API key</p>
        </div>

        <div className="product-scene" aria-label="Cursivis result panel preview">
          <div className="selection-line selection-line-one" />
          <div className="selection-line selection-line-two" />
          <div className="selection-line selection-line-three" />
          <div className="cursor" aria-hidden="true" />
          <div className="orb-preview" aria-hidden="true">
            <span className="orb-core" />
            <span className="orb-label">Improving writing</span>
          </div>
          <div className="result-preview">
            <div className="result-header">
              <div className="mini-brand">
                <span className="brand-mark tiny"><span /></span>
                Cursivis
              </div>
              <div className="result-commands">
                <span>Undo</span>
                <span className="active">Insert</span>
                <span>More Options</span>
                <b>×</b>
              </div>
            </div>
            <div className="result-body">
              <small>OUTPUT / IMPROVE WRITING · TEXT</small>
              <p>
                Clearer writing, stronger structure, and the original meaning—ready
                to use where you were already working.
              </p>
              <div className="result-footer">
                <span>Copied</span>
                <span>Esc to close</span>
              </div>
            </div>
          </div>
        </div>
      </section>

      <section className="trust-strip" aria-label="Product principles">
        <div className="shell trust-items">
          <span><b>01</b> Cursor-first</span>
          <span><b>02</b> OpenAI-native</span>
          <span><b>03</b> Local-key storage</span>
          <span><b>04</b> User-controlled actions</span>
        </div>
      </section>

      <section className="section shell" id="features">
        <div className="section-heading">
          <p className="section-kicker">One interaction system</p>
          <h2>Less switching. More doing.</h2>
          <p>
            Four focused modes share one captured context, one visual language,
            and one clear safety boundary.
          </p>
        </div>
        <div className="feature-grid">
          {features.map((feature) => (
            <article className="feature-card" key={feature.number}>
              <span className="feature-number">{feature.number}</span>
              <div className={`feature-icon feature-icon-${feature.number}`} aria-hidden="true">
                <span />
              </div>
              <h3>{feature.title}</h3>
              <p>{feature.body}</p>
            </article>
          ))}
        </div>
      </section>

      <section className="section install-section" id="install">
        <div className="shell install-grid">
          <div className="install-copy">
            <p className="section-kicker">Install in minutes</p>
            <h2>Your workflow stays yours.</h2>
            <p>
              The Windows installer adds Cursivis to your Start Menu, offers a
              desktop shortcut, and can launch it automatically after sign-in.
            </p>
            <a className="button button-primary" href={releaseDownload}>
              Download the beta
              <span aria-hidden="true">→</span>
            </a>
          </div>
          <ol className="steps">
            <li>
              <span>1</span>
              <div>
                <h3>Run the installer</h3>
                <p>Open the downloaded setup file and approve the normal Windows prompt.</p>
              </div>
            </li>
            <li>
              <span>2</span>
              <div>
                <h3>Add your OpenAI key</h3>
                <p>First launch opens Settings so you can save and test your own API key.</p>
              </div>
            </li>
            <li>
              <span>3</span>
              <div>
                <h3>Select, trigger, continue</h3>
                <p>Highlight text or capture a region, then let Cursivis work beside your cursor.</p>
              </div>
            </li>
          </ol>
        </div>
      </section>

      <section className="notice shell" aria-labelledby="beta-note-title">
        <div className="notice-icon" aria-hidden="true">i</div>
        <div>
          <h2 id="beta-note-title">A note about the Windows beta</h2>
          <p>
            Windows Defender SmartScreen may show an <strong>Unknown Publisher</strong>
            warning because this beta is not code-signed yet. Download only from the
            official GitHub Release linked on this page.
          </p>
        </div>
      </section>

      <section className="privacy shell">
        <div>
          <p className="section-kicker">Privacy by design</p>
          <h2>Your key stays local.</h2>
        </div>
        <p>
          You supply your own OpenAI API key. Cursivis encrypts it for your current
          Windows account and stores it locally—never in the browser extension or
          this website. Screen capture is explicit and saved Live Mode memories are
          opt-in, inspectable, and deletable.
        </p>
      </section>

      <footer>
        <div className="shell footer-grid">
          <a className="brand" href="#top">
            <span className="brand-mark" aria-hidden="true"><span /></span>
            <span>Cursivis</span>
          </a>
          <p>OpenAI-native productivity for Windows.</p>
          <div>
            <a href="https://github.com/UnknownGod2011/openai-cursivis">GitHub</a>
            <span>Beta · 2026</span>
          </div>
        </div>
      </footer>
    </main>
  );
}
