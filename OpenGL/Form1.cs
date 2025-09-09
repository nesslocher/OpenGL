using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;
using OpenTK;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;


namespace OpenGL
{
    public partial class Form1 : Form
    {
        //shaders
        private const string VertexSrc = 
            @"#version 330 core

            layout (location=0) in vec3 Pos;
            layout (location=1) in vec3 Color;
            layout (location=2) in vec2 UV;
            layout (location=3) in vec3 Normal;

            uniform mat4 Projection;
            uniform mat4 View;
            uniform mat4 Model;

            uniform float BulgeAmount;
            uniform float BendAmount;
            uniform float TwistAmount;
            uniform bool IsGrid;

            out vec3 vertexColor;
            out vec2 uv;
            out vec3 vNormal;
            out vec3 vWorldPos;

            void main() {

                vec3 scaledPos = Pos;

                float angleTwist = IsGrid ? 0.0 : scaledPos.y * TwistAmount;
                float c = cos(angleTwist), s = sin(angleTwist);
                mat4 twist = mat4(
                    c, 0.0, -s, 0.0,
                    0.0, 1.0, 0.0, 0.0,
                    s, 0.0,  c, 0.0,
                    0.0, 0.0, 0.0, 1.0
                );
                vec4 twistedPos = twist * vec4(scaledPos, 1.0);

                float bendAngle = IsGrid ? 0.0 : scaledPos.z * BendAmount;
                float cb = cos(bendAngle), sb = sin(bendAngle);
                mat4 bend = mat4(
                    1.0, 0.0, 0.0, 0.0,
                    0.0,  cb, -sb, 0.0,
                    0.0,  sb,  cb, 0.0,
                    0.0, 0.0, 0.0, 1.0
                );
                vec4 localPos = bend * twistedPos;

                //bulge
                if(!IsGrid){
                    vec3 fromCenter = localPos.xyz;
                    float dist = length(fromCenter);
                    float bulge = exp(-dist * dist * 4.0) * BulgeAmount;
                    localPos.xyz += fromCenter * bulge;
                }

                vec4 worldPos = Model * localPos;
                vWorldPos = worldPos.xyz;

                mat3 linearPart = mat3(Model) * mat3(bend) * mat3(twist);
                mat3 nrmMat = transpose(inverse(linearPart));
                vNormal = normalize(nrmMat * Normal);

                gl_Position = Projection * View * worldPos;

                uv = UV;
                vertexColor = Color;
            }";

        private const string FragmentSrc =
            @"#version 330 core

            uniform sampler2D Texture;
            uniform vec4  Display;
            uniform bool  IsGrid;

            uniform vec3  LightPos;
            uniform vec3  ViewPos;
            uniform float shininess;
            uniform vec3  SpecularColor;

            in vec3 vertexColor;
            in vec2 uv;
            in vec3 vWorldPos;

            out vec4 FragColor;

            void main()
            {
                if (IsGrid) {
                    FragColor = vec4(vertexColor, 1.0);
                    return;
                }

                //basefarve
                vec3 baseColor = mix(vertexColor, texture(Texture, uv).rgb,
                                     clamp(abs(Display.a), 0.0, 1.0));

                //normal fra den deformerede world-position
                vec3 Ng = normalize(cross(dFdx(vWorldPos), dFdy(vWorldPos)));
                vec3 V  = normalize(ViewPos - vWorldPos);
                vec3 N  = faceforward(Ng, V, Ng);

                //lys
                vec3 L = normalize(LightPos - vWorldPos);
                float NdotL = max(dot(N, L), 0.0);

                //Blinn-Phong
                float ambient = 0.10;
                vec3  diffuse = NdotL * baseColor;

                vec3  H = normalize(L + V);
                float specPow = max(dot(N, H), 0.0);
                float spec = (NdotL > 0.0) ? pow(specPow, max(shininess, 1.0)) : 0.0;

                vec3 lightColor = Display.rgb;
                vec3 color = lightColor * (ambient * baseColor + diffuse) + spec * SpecularColor;

                FragColor = vec4(color, 1.0);
            }";


        //GPU handles 
        private int _program;
        private int _vao, _vbo;

        //uniform locations
        private int uScale, uProjection, uModel, uView, uDisplay, uTexture, uIsGrid, uTwist, uBend, uBulge;

        //CPU-side state
        private readonly List<float> verticesModel = new List<float>(); 
        private readonly List<float> verticesGround = new List<float>();
        private const int STRIDE = 11 * sizeof(float);

        private int groundVertexCount = 0; 
        private int modelVertexCount = 0;

        private Vector4 Angle = new Vector4(0, 0, 0, 1);
        private Vector3 Scale = new Vector3(1, 1, 1);
        private Matrix4 View;
        private Matrix4 Projection;
        private Matrix4 Model;
        private Vector4 Display = new Vector4(1, 1, 1, 0); 
        private float TwistAmount = 0f, BendAmount = 0f, BulgeAmount = 0f;

        private readonly List<int> _textures = new List<int>();
        private int _currentTextureIndex = -1;

        private bool _showGrid = true;

        //mouse control
        private bool _dragging = false;
        private Point _lastMouse;
        private float _yaw = 0f;   
        private float _pitch = 0f;  
        private float _distance = -5f; 

        private enum ShapeType { Triangle, Quad, Box, SubdividedBox, Cylinder }
        private ShapeType _activeShape = ShapeType.Triangle;

        //UI controls 
        private TrackBar _tbTwist, _tbBend, _tbBulge, _tbMix;
        private Label _lblTwist, _lblBend, _lblBulge, _lblMix;
        private CheckBox _chkGrid;

        public Form1()
        {
            InitializeComponent();


            glControl1.Dock = DockStyle.Fill;

            glControl1.Load += Gl_Load;
            glControl1.Resize += Gl_Resize;
            glControl1.Paint += Gl_Paint;


            //mouse
            glControl1.MouseDown += GlControl1_MouseDown;
            glControl1.MouseUp += GlControl1_MouseUp;
            glControl1.MouseMove += GlControl1_MouseMove;
            glControl1.MouseWheel += GlControl1_MouseWheel;

            //UI panel til modifiers 
            var panel = new Panel
            {
                Width = 260,
                Dock = DockStyle.Right,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            this.Controls.Add(panel);
            panel.BringToFront();

            Label MakeLbl(string text, int top)
            {
                var l = new Label { Text = text, Left = 12, Top = top, Width = 200, ForeColor = Color.LightBlue, BackColor = Color.Transparent };
                panel.Controls.Add(l);
                return l;
            }
            TrackBar MakeTb(int top, int min, int max, int value)
            {
                var tb = new TrackBar
                {
                    Left = 12,
                    Top = top,
                    Width = panel.Width - 24,
                    Minimum = min,
                    Maximum = max,
                    TickFrequency = Math.Max(1, (max - min) / 10),
                    Value = value
                };
                panel.Controls.Add(tb);
                return tb;
            }

            //ayoutcursor
            int y = 20;

            //figur vælgeren
            var lblShape = MakeLbl("Shape:", y);
            y += 36;

            var shapeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 12,
                Top = y,
                Width = panel.Width - 24
            };
            shapeCombo.Items.AddRange(new object[] { "Triangle", "Quad", "Box", "Subdivided Box", "Cylinder" });
            shapeCombo.SelectedIndex = (int)_activeShape;
            shapeCombo.SelectedIndexChanged += (s, e) =>
            {
                _activeShape = (ShapeType)shapeCombo.SelectedIndex;
                RebuildModelAndUpload();
                glControl1.Invalidate();
            };
            panel.Controls.Add(shapeCombo);

            y += 40;

            //sliders og labels
            _lblTwist = MakeLbl("Twist (rad/unit Y): 0.00", y);
            y += 24;
            _tbTwist = MakeTb(y, -400, 400, 0);
            y += 70;

            _lblBend = MakeLbl("Bend (rad/unit Z): 0.00", y);
            y += 24;
            _tbBend = MakeTb(y, -400, 400, 0);
            y += 70;

            _lblBulge = MakeLbl("Bulge: 0.00", y);
            y += 24;
            _tbBulge = MakeTb(y, -300, 500, 0);
            y += 70;

            _lblMix = MakeLbl("Texture mix: 0.00", y);
            y += 24;
            _tbMix = MakeTb(y, 0, 100, 0);
            y += 70;

            //grid checkbox + reset
            _chkGrid = new CheckBox
            {
                Text = "Show grid",
                Left = 12,
                Top = y,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Checked = _showGrid,
                AutoSize = true
            };
            _chkGrid.CheckedChanged += (s, e) => { _showGrid = _chkGrid.Checked; glControl1.Invalidate(); };
            panel.Controls.Add(_chkGrid);
            y += 32;

            var btnReset = new Button { Text = "Reset modifiers", Left = 12, Top = y, Width = panel.Width - 24, Height = 40, ForeColor = Color.LightBlue };
            btnReset.Click += (s, e) =>
            {
                _tbTwist.Value = 0; _tbBend.Value = 0; _tbBulge.Value = 0; _tbMix.Value = 0;
                OnModifiersChanged(null, EventArgs.Empty);
            };
            panel.Controls.Add(btnReset);

            //bind events TIL SIDST, når alle controls findes
            _tbTwist.ValueChanged += OnModifiersChanged;
            _tbBend.ValueChanged += OnModifiersChanged;
            _tbBulge.ValueChanged += OnModifiersChanged;
            _tbMix.ValueChanged += OnModifiersChanged;


        }

        #region load, resize, paint
        private void Gl_Load(object sender, EventArgs e)
        {
            glControl1.MakeCurrent();

            GL.ClearColor(0.3f, 0.3f, 0.6f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);

            //kald metoden til at bygge shader-programmet
            _program = BuildProgram(VertexSrc, FragmentSrc);
            GL.UseProgram(_program);

            int uLightPos = GL.GetUniformLocation(_program, "LightPos");
            int uShiny = GL.GetUniformLocation(_program, "shininess");
            int uSpecCol = GL.GetUniformLocation(_program, "SpecularColor");
            if (uLightPos >= 0) GL.Uniform3(uLightPos, new Vector3(0f, -2f, -2f)); //lys position
            if (uShiny >= 0) GL.Uniform1(uShiny, 32f);
            if (uSpecCol >= 0) GL.Uniform3(uSpecCol, new Vector3(1f, 1f, 1f));

            if (uTexture >= 0) GL.Uniform1(uTexture, 0);


            uProjection = GL.GetUniformLocation(_program, "Projection");
            uModel = GL.GetUniformLocation(_program, "Model");
            uView = GL.GetUniformLocation(_program, "View");      
            uDisplay = GL.GetUniformLocation(_program, "Display");
            uTexture = GL.GetUniformLocation(_program, "Texture");
            uIsGrid = GL.GetUniformLocation(_program, "IsGrid");
            uTwist = GL.GetUniformLocation(_program, "TwistAmount");
            uBend = GL.GetUniformLocation(_program, "BendAmount");
            uBulge = GL.GetUniformLocation(_program, "BulgeAmount");

            Model = Matrix4.Identity;
            GL.UniformMatrix4(uModel, false, ref Model);
            UploadProjection();
            GL.Uniform4(uDisplay, Display);
            GL.Uniform1(uTwist, TwistAmount);
            GL.Uniform1(uBend, BendAmount);
            GL.Uniform1(uBulge, BulgeAmount);

            _distance = -5f;
            _yaw = 0f; _pitch = 0f;
            UploadView();


            verticesModel.Clear();
            verticesGround.Clear();

            //Grid 
            CreateGroundGrid(width: 100, depth: 100, divX: 100, divZ: 100, yOffset: -1f);
            CreateMountainGrid(-0f, -0.4f, -40f, 35f, 30f, 30, 20, new float[] { 1f, 0f, 1f }); //lilla
            CreateMountainGrid(-10f, -0.6f, -40f, 15f, 10f, 20, 10, new float[] { 1f, 1f, 0f }); //gul
            CreateMountainGrid(20f, -0.5f, -40f, 28f, 22f, 20, 20, new float[] { 0f, 1f, 1f }); //teal
            CreateSynthwaveSun(0.0f, -0.6f, -40.0f, 10f, 70, new float[] { 0.7f, 0.0f, 0.0f }, new float[] { 1.0f, 1.0f, 0.0f });

            //model
            CreateTriangle(1.0f, 1.0f, 1.0f);

            groundVertexCount = verticesGround.Count / 11;
            modelVertexCount = verticesModel.Count / 11;

            var all = new float[verticesGround.Count + verticesModel.Count];
            verticesGround.CopyTo(all, 0);
            verticesModel.CopyTo(all, verticesGround.Count);

            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, all.Length * sizeof(float), all, BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0); //position
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, STRIDE, 0);

            GL.EnableVertexAttribArray(1); //color
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, STRIDE, 3 * sizeof(float));

            GL.EnableVertexAttribArray(2); //uv
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, STRIDE, 6 * sizeof(float));

            GL.EnableVertexAttribArray(3); //normal
            GL.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, STRIDE, 8 * sizeof(float));

            GL.BindVertexArray(0);
        }

        private void Gl_Resize(object sender, EventArgs e)
        {
            GL.Viewport(0, 0, glControl1.Width, glControl1.Height);
            UploadProjection();
        }


        private void Gl_Paint(object sender, PaintEventArgs e)
        {
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.UseProgram(_program);
            GL.BindVertexArray(_vao);

            GL.UniformMatrix4(uView, false, ref View);


            //grid
            if (_showGrid && groundVertexCount > 0)
            {
                GL.DepthMask(false);
                GL.Uniform1(uIsGrid, 1);
                var I = Matrix4.Identity;
                GL.UniformMatrix4(uModel, false, ref I);
                GL.DrawArrays(PrimitiveType.Lines, 0, groundVertexCount);
                GL.DepthMask(true);
            }

            //model
            GL.Uniform1(uIsGrid, 0);
            GL.UniformMatrix4(uModel, false, ref Model);

            if (_currentTextureIndex >= 0 && _currentTextureIndex < _textures.Count)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _textures[_currentTextureIndex]);
                if (uTexture >= 0) GL.Uniform1(uTexture, 0);
            }
            GL.Uniform4(uDisplay, Display); 

            if (modelVertexCount > 0)
                GL.DrawArrays(PrimitiveType.Triangles, groundVertexCount, modelVertexCount);

            GL.BindVertexArray(0);
            glControl1.SwapBuffers();
            glControl1.Invalidate();
        }
        #endregion

        private void UploadView()
        {
            //kamera omkring (0,0,0)
            float r = MathF.Max(0.001f, -_distance);
            float cx = r * MathF.Cos(_pitch) * MathF.Sin(_yaw);
            float cy = r * MathF.Sin(_pitch);
            float cz = r * MathF.Cos(_pitch) * MathF.Cos(_yaw);

            var eye = new Vector3(cx, cy, cz);
            var target = Vector3.Zero;
            var up = Vector3.UnitY;

            View = Matrix4.LookAt(eye, target, up);
            GL.UniformMatrix4(uView, false, ref View);

            //fragment shader har brug for kameraets world-pos
            int uViewPos = GL.GetUniformLocation(_program, "ViewPos");
            if (uViewPos >= 0) GL.Uniform3(uViewPos, eye);
        }

        private void UploadProjection()
        {
            float fov = MathHelper.DegreesToRadians(45f);
            float aspect = Math.Max(1, glControl1.Width) / (float)Math.Max(1, glControl1.Height);
            Projection = Matrix4.CreatePerspectiveFieldOfView(fov, aspect, 0.1f, 100f);
            GL.UniformMatrix4(uProjection, false, ref Projection);
        }

        //modifiers - twist, bend, bulge
        private void OnModifiersChanged(object sender, EventArgs e)
        {
            float twist = _tbTwist.Value / 100f; 
            float bend = _tbBend.Value / 100f; 
            float bulge = _tbBulge.Value / 100f; 
            float mix = _tbMix.Value / 100f;

            _lblTwist.Text = $"Twist (rad/unit Y): {twist:0.00}";
            _lblBend.Text = $"Bend (rad/unit Z): {bend:0.00}";
            _lblBulge.Text = $"Bulge: {bulge:0.00}";
            _lblMix.Text = $"Texture mix: {mix:0.00}";

            glControl1.MakeCurrent();

            SetTwist(twist);
            SetBend(bend);
            SetBulge(bulge);

            Display.W = mix;                 
            GL.Uniform4(uDisplay, Display);  

            glControl1.Invalidate();
        }

        #region Mouse interaction   
        private void GlControl1_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _lastMouse = e.Location;
            }
        }

        private void GlControl1_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                _dragging = false;
        }

        private void GlControl1_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            float dx = e.X - _lastMouse.X;
            float dy = e.Y - _lastMouse.Y;
            _lastMouse = e.Location;

            const float s = 0.01f;
            _yaw -= dx * s;
            _pitch -= dy * s;
            _pitch = Math.Clamp(_pitch, -1.57f, 1.57f);

            UploadView();
            glControl1.Invalidate();
        }


        private void GlControl1_MouseWheel(object sender, MouseEventArgs e)
        {
            float step = 0.5f * (e.Delta / 120f);
            _distance += step;       
            UploadView();              
            glControl1.Invalidate();
        }


        #endregion


        //selve bygning af shader-programmet
        private static int BuildProgram(string vs, string fs)
        {
            int v = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(v, vs);
            GL.CompileShader(v);
            GL.GetShader(v, ShaderParameter.CompileStatus, out int okV);
            if (okV == 0) throw new Exception(GL.GetShaderInfoLog(v));

            int f = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(f, fs);
            GL.CompileShader(f);
            GL.GetShader(f, ShaderParameter.CompileStatus, out int okF);
            if (okF == 0) throw new Exception(GL.GetShaderInfoLog(f));

            int p = GL.CreateProgram();
            GL.AttachShader(p, v);
            GL.AttachShader(p, f);
            GL.LinkProgram(p);
            GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int okP);
            if (okP == 0) throw new Exception(GL.GetProgramInfoLog(p));

            GL.DeleteShader(v);
            GL.DeleteShader(f);
            return p;
        }

        //model manipulation
        private void RebuildModelAndUpload()
        {
            verticesModel.Clear();

            switch (_activeShape)
            {
                case ShapeType.Triangle:
                    CreateTriangle(1.0f, 1.0f, 0.0f);
                    break;
                case ShapeType.Quad:
                    CreateQuad(1.0f, 1.0f, 0.0f);
                    break;
                case ShapeType.Box:
                    CreateBox(1.0f, 1.0f, 1.0f);
                    break;
                case ShapeType.SubdividedBox:
                    CreateSubdividedBox(1.2f, 1.0f, 1.2f, 8, 8);
                    break;
                case ShapeType.Cylinder:
                    CreateCylinder(0.5f, 1.0f, 64);
                    break;
            }

            modelVertexCount = verticesModel.Count / 11;

            var all = new float[verticesGround.Count + verticesModel.Count];
            verticesGround.CopyTo(all, 0);
            verticesModel.CopyTo(all, verticesGround.Count);

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

            GL.BufferData(BufferTarget.ArrayBuffer, all.Length * sizeof(float), all, BufferUsageHint.StaticDraw);

            GL.BindVertexArray(0);
        }


        #region model konstruktion 

        private bool _activeGround = false;
        private List<float> active => _activeGround ? verticesGround : verticesModel;

        private void AddVertex(float x, float y, float z, float r, float g, float b, float u, float v, float nx, float ny, float nz)
        {
            active.AddRange(new[] { x, y, z, r, g, b, u, v, nx, ny, nz });
        }

        private void AddTriangle(
            float x1, float y1, float z1, float r1, float g1, float b1, float u1, float v1,
            float x2, float y2, float z2, float r2, float g2, float b2, float u2, float v2,
            float x3, float y3, float z3, float r3, float g3, float b3, float u3, float v3,
            float nx, float ny, float nz)
        {
            AddVertex(x1, y1, z1, r1, g1, b1, u1, v1, nx, ny, nz);
            AddVertex(x2, y2, z2, r2, g2, b2, u2, v2, nx, ny, nz);
            AddVertex(x3, y3, z3, r3, g3, b3, u3, v3, nx, ny, nz);
        }

        private void AddQuad(
            float x1, float y1, float z1, float r1, float g1, float b1, float u1, float v1,
            float x2, float y2, float z2, float r2, float g2, float b2, float u2, float v2,
            float x3, float y3, float z3, float r3, float g3, float b3, float u3, float v3,
            float x4, float y4, float z4, float r4, float g4, float b4, float u4, float v4,
            float nx, float ny, float nz)
        {
            AddTriangle(x1, y1, z1, r1, g1, b1, u1, v1, x3, y3, z3, r3, g3, b3, u3, v3, x2, y2, z2, r2, g2, b2, u2, v2, nx, ny, nz);
            AddTriangle(x1, y1, z1, r1, g1, b1, u1, v1, x4, y4, z4, r4, g4, b4, u4, v4, x3, y3, z3, r3, g3, b3, u3, v3, nx, ny, nz);
        }

        // Shapes
        private void CreateTriangle(float width, float height, float depth)
        {
            float w = width * 0.5f;
            float h = height * 0.5f;
            float d = 0f;
            float nx = 0, ny = 0, nz = 1;
            AddTriangle(
                0f, h, d, 1, 0, 0, 0.5f, 1f,
               -w, -h, d, 0, 1, 0, 0f, 0f,
                w, -h, d, 0, 0, 1, 1f, 0f,
                nx, ny, nz);
        }

        private void CreateQuad(float width, float height, float depth)
        {
            float w = width * 0.5f, h = height * 0.5f, d = 0f;
            float nx = 0, ny = 0, nz = 1;
            AddQuad(
                -w, h, d, 1, 0, 0, 0, 1,
                 w, h, d, 1, 1, 0, 1, 1,
                 w, -h, d, 0, 0, 1, 1, 0,
                -w, -h, d, 0, 1, 0, 0, 0,
                nx, ny, nz);
        }

        private void CreateBox(float width, float height, float depth)
        {
            float w = width * 0.5f,  h = height * 0.5f,  d = depth * 0.5f;

            // +Z
            AddQuad(-w, -h, d, 1, 0, 0, 0, 0, -w, h, d, 1, 0, 0, 0, 1, w, h, d, 1, 0, 0, 1, 1, w, -h, d, 1, 0, 0, 1, 0, 0, 0, 1);
            // -Z
            AddQuad(w, -h, -d, 0, 1, 0, 0, 0, w, h, -d, 0, 1, 0, 0, 1, -w, h, -d, 0, 1, 0, 1, 1, -w, -h, -d, 0, 1, 0, 1, 0, 0, 0, -1);
            // +Y
            AddQuad(-w, h, d, 0, 0, 1, 0, 0, -w, h, -d, 0, 0, 1, 0, 1, w, h, -d, 0, 0, 1, 1, 1, w, h, d, 0, 0, 1, 1, 0, 0, 1, 0);
            // -Y
            AddQuad(-w, -h, -d, 1, 0.5f, 0, 0, 0, -w, -h, d, 1, 0.5f, 0, 0, 1, w, -h, d, 1, 0.5f, 0, 1, 1, w, -h, -d, 1, 0.5f, 0, 1, 0, 0, -1, 0);
            // -X
            AddQuad(-w, -h, -d, .5f, 0, .5f, 0, 0, -w, h, -d, .5f, 0, .5f, 0, 1, -w, h, d, .5f, 0, .5f, 1, 1, -w, -h, d, .5f, 0, .5f, 1, 0, -1, 0, 0);
            // +X
            AddQuad(w, -h, d, 1, 1, 0, 0, 0, w, h, d, 1, 1, 0, 0, 1, w, h, -d, 1, 1, 0, 1, 1, w, -h, -d, 1, 1, 0, 1, 0, 1, 0, 0);
        }

        private void CreateSubdividedBox(float width, float height, float depth, int divX, int divY)
        {
            verticesModel.Clear();
            float x0 = -width / 2f, y0 = -height / 2f, z0 = -depth / 2f;
            float dx = width / divX;
            float dy = height / divY;
            float dz = depth / divX;

            // +X face (Right)
            for (int j = 0; j < divY; j++)
            {
                for (int k = 0; k < divX; k++)
                {
                    float y = y0 + j * dy;
                    float z = z0 + k * dz;
                    AddQuad(
                        x0 + width, y, z, 1, 0, 0, (float)k / divX, (float)j / divY,
                        x0 + width, y, z + dz, 1, 0, 0, (float)k / divX, (float)(j + 1) / divY,
                        x0 + width, y + dy, z + dz, 1, 0, 0, (float)(k + 1) / divX, (float)(j + 1) / divY,
                        x0 + width, y + dy, z, 1, 0, 0, (float)(k + 1) / divX, (float)j / divY,
                        1, 0, 0);
                }
            }

            // -X face (Left)
            for (int j = 0; j < divY; j++)
            {
                for (int k = 0; k < divX; k++)
                {
                    float y = y0 + j * dy;
                    float z = z0 + k * dz;
                    AddQuad(
                        x0, y, z, 1, 0, 0, (float)k / divX, (float)j / divY,
                        x0, y + dy, z, 1, 0, 0, (float)(k + 1) / divX, (float)j / divY,
                        x0, y + dy, z + dz, 1, 0, 0, (float)(k + 1) / divX, (float)(j + 1) / divY,
                        x0, y, z + dz, 1, 0, 0, (float)k / divX, (float)(j + 1) / divY,
                        -1, 0, 0);
                }
            }

            // +Y face (Top)
            for (int i = 0; i < divX; i++)
            {
                for (int k = 0; k < divX; k++)
                {
                    float x = x0 + i * dx;
                    float z = z0 + k * dz;
                    AddQuad(
                        x, y0 + height, z, 0, 1, 0, (float)i / divX, (float)k / divX,
                        x + dx, y0 + height, z, 0, 1, 0, (float)(i + 1) / divX, (float)k / divX,
                        x + dx, y0 + height, z + dz, 0, 1, 0, (float)(i + 1) / divX, (float)(k + 1) / divX,
                        x, y0 + height, z + dz, 0, 1, 0, (float)i / divX, (float)(k + 1) / divX,
                        0, 1, 0);
                }
            }

            // -Y face (Bottom)
            for (int i = 0; i < divX; i++)
            {
                for (int k = 0; k < divX; k++)
                {
                    float x = x0 + i * dx;
                    float z = z0 + k * dz;
                    AddQuad(
                        x, y0, z, 0, 0, 1, (float)i / divX, (float)k / divX,
                        x, y0, z + dz, 0, 0, 1, (float)i / divX, (float)(k + 1) / divX,
                        x + dx, y0, z + dz, 0, 0, 1, (float)(i + 1) / divX, (float)(k + 1) / divX,
                        x + dx, y0, z, 0, 0, 1, (float)(i + 1) / divX, (float)k / divX,
                        0, -1, 0);
                }
            }

            // +Z face (Front)
            for (int i = 0; i < divX; i++)
            {
                for (int j = 0; j < divY; j++)
                {
                    float x = x0 + i * dx;
                    float y = y0 + j * dy;
                    AddQuad(
                        x, y, z0 + depth, 1, 0, 0, (float)i / divX, (float)j / divY,
                        x, y + dy, z0 + depth, 1, 0, 0, (float)i / divX, (float)(j + 1) / divY,
                        x + dx, y + dy, z0 + depth, 1, 0, 0, (float)(i + 1) / divX, (float)(j + 1) / divY,
                        x + dx, y, z0 + depth, 1, 0, 0, (float)(i + 1) / divX, (float)j / divY,
                        0, 0, 1);
                }
            }

            // -Z face (Back)
            for (int i = 0; i < divX; i++)
            {
                for (int j = 0; j < divY; j++)
                {
                    float x = x0 + i * dx;
                    float y = y0 + j * dy;
                    AddQuad(
                        x, y, z0, 1, 0, 0, (float)i / divX, (float)j / divY,
                        x + dx, y, z0, 1, 0, 0, (float)(i + 1) / divX, (float)j / divY,
                        x + dx, y + dy, z0, 1, 0, 0, (float)(i + 1) / divX, (float)(j + 1) / divY,
                        x, y + dy, z0, 1, 0, 0, (float)i / divX, (float)(j + 1) / divY,
                        0, 0, -1);
                }
            }

        }

        private void CreateCylinder(float radius, float height, int segments)
        {
            float h = height * 0.5f;
            var color = new[] { 0f, 0.9f, 0.9f };
            var up = new[] { 0f, 1f, 0f };
            var down = new[] { 0f, -1f, 0f };
            for (int i = 0; i < segments; i++)
            {
                float a1 = (i / (float)segments) * MathF.PI * 2f;
                float a2 = ((i + 1) / (float)segments) * MathF.PI * 2f;
                float x1 = MathF.Cos(a1) * radius, z1 = MathF.Sin(a1) * radius;
                float x2 = MathF.Cos(a2) * radius, z2 = MathF.Sin(a2) * radius;

                //top
                AddTriangle(0, h, 0, color[0], color[1], color[2], .5f, .5f,
                            x2, h, z2, color[0], color[1], color[2], (x2 + 1) / 2f, (z2 + 1) / 2f,
                            x1, h, z1, color[0], color[1], color[2], (x1 + 1) / 2f, (z1 + 1) / 2f,
                            up[0], up[1], up[2]);
                //bund
                AddTriangle(0, -h, 0, color[0], color[1], color[2], .5f, .5f,
                            x1, -h, z1, color[0], color[1], color[2], (x1 + 1) / 2f, (z1 + 1) / 2f,
                            x2, -h, z2, color[0], color[1], color[2], (x2 + 1) / 2f, (z2 + 1) / 2f,
                            down[0], down[1], down[2]);

                //side
                var n1 = Normalize(x1, 0, z1);
                var n2 = Normalize(x2, 0, z2);
                var n = Normalize((n1.X + n2.X) / 2f, 0, (n1.Z + n2.Z) / 2f);
                AddQuad(
                    x2, -h, z2, color[0], color[1], color[2], (i + 1) / (float)segments, 0f,
                    x2, h, z2, color[0], color[1], color[2], (i + 1) / (float)segments, 1f,
                    x1, h, z1, color[0], color[1], color[2], i / (float)segments, 1f,
                    x1, -h, z1, color[0], color[1], color[2], i / (float)segments, 0f,
                    n.X, n.Y, n.Z
                );
            }
        }

        #endregion

        private static Vector3 Normalize(float x, float y, float z)
        {
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len <= 1e-6f) return Vector3.Zero;
            return new Vector3(x / len, y / len, z / len);
        }



        #region grid og pynt
        //grid og pynt
        private void AddGridLine(float x1, float y1, float z1, float x2, float y2, float z2, float r, float g, float b)
        {
            _activeGround = true;
            active.AddRange(new[] { x1, y1, z1, r, g, b, 0, 0, 0, 1, 0 });
            active.AddRange(new[] { x2, y2, z2, r, g, b, 0, 0, 0, 1, 0 });
            _activeGround = false;
        }

        private void CreateGroundGrid(float width, float depth, int divX, int divZ, float yOffset = -0.5f)
        {
            _activeGround = true;
            verticesGround.Clear();

            float dx = width / divX;
            float dz = depth / divZ;
            float x0 = -width / 2f;
            float z0 = -depth / 2f;
            var color = new[] { 0f, 1f, 0f };

            for (int i = 0; i <= divX; i++)
            {
                float x = x0 + i * dx;
                AddGridLine(x, yOffset, z0, x, yOffset, z0 + depth, color[0], color[1], color[2]);
            }
            for (int j = 0; j <= divZ; j++)
            {
                float z = z0 + j * dz;
                AddGridLine(x0, yOffset, z, x0 + width, yOffset, z, color[0], color[1], color[2]);
            }
            _activeGround = false;
        }

        private void CreateMountainGrid(float centerX, float baseY, float centerZ, float width, float height, int segmentsX, int segmentsY, float[] color)
        {
            float halfW = width * 0.5f;
            //lodrette
            for (int i = 0; i <= segmentsX; i++)
            {
                float t = i / (float)segmentsX;
                float x = centerX - halfW + t * width;
                float peakX = centerX;
                float peakY = baseY + height;
                float dx = x - peakX;
                float yTop = peakY - Math.Abs(dx) * (height / halfW);
                AddGridLine(x, baseY, centerZ, x, yTop, centerZ, color[0], color[1], color[2]);
            }
            //vandrette
            for (int j = 1; j <= segmentsY; j++)
            {
                float t = j / (float)segmentsY;
                float y = baseY + t * height;
                float halfSpan = (1 - t) * halfW;
                float x1 = centerX - halfSpan;
                float x2 = centerX + halfSpan;
                AddGridLine(x1, y, centerZ, x2, y, centerZ, color[0], color[1], color[2]);
            }
        }

        private void CreateSynthwaveSun(float cx, float cy, float cz, float radius = 1.2f, int lines = 30, float[] colorTop = null, float[] colorBottom = null)
        {
            colorTop ??= new float[] { 1f, 0f, 0f };
            colorBottom ??= new float[] { 1f, 0.5f, 0f };
            for (int i = 0; i < lines; i++)
            {
                float t = i / (float)lines;
                float y = cy + radius - t * radius * 2f;
                if (y < cy) continue;
                float dy = y - cy;
                float halfWidth = MathF.Sqrt(MathF.Max(radius * radius - dy * dy, 0f));
                float x1 = cx - halfWidth;
                float x2 = cx + halfWidth;
                float r = colorTop[0] * (1 - t) + colorBottom[0] * t;
                float g = colorTop[1] * (1 - t) + colorBottom[1] * t;
                float b = colorTop[2] * (1 - t) + colorBottom[2] * t;
                AddGridLine(x1, y, cz, x2, y, cz, r, g, b);
            }
        }

        #endregion


        public void SetScale(float sx, float sy, float sz)
        {
            Scale = new Vector3(sx, sy, sz);
        }

        public void SetTranslation(float tx, float ty, float tz)
        {
            Model = Matrix4.CreateTranslation(tx, ty, tz);
        }

        public void SetAngles(float ax, float ay, float az)
        {
            Angle = new Vector4(ax, ay, az, 1);
        }

        public void SetTwist(float val) 
        { 
            TwistAmount = val; 
            GL.Uniform1(uTwist, TwistAmount); 
        }

        public void SetBend(float val) 
        { 
            BendAmount = val; 
            GL.Uniform1(uBend, BendAmount); 
        }

        public void SetBulge(float val) 
        { 
            BulgeAmount = val; 
            GL.Uniform1(uBulge, BulgeAmount); 
        }

        public void SetDisplay(float r, float g, float b, float mix) 
        { 
            Display = new Vector4(r, g, b, mix); 
        }

    }
}
