using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OpenTK;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace OpenGL
{
    public partial class Form1 : Form
    {
        private const string VertexSrc =
            @"#version 330 core

            layout (location=0) in vec3 Pos;
            layout (location=1) in vec3 Color;
            layout (location=2) in vec2 UV; 
            layout (location=3) in vec3 Normal;

            uniform mat4 Projection;
            uniform mat4 View;
            uniform mat4 Model;

            out vec3 vertexColor;
            out vec3 vNormal;
            out vec3 vWorldPos;

            void main() {
                vec4 worldPos = Model * vec4(Pos, 1.0);
                vWorldPos = worldPos.xyz;

//normal i world space
                mat3 normalMatrix = transpose(inverse(mat3(Model)));
                vNormal = normalize(normalMatrix * Normal);

                gl_Position = Projection * View * worldPos;

                vertexColor = Color;
            }";

        private const string FragmentSrc =
            @"#version 330 core

            uniform bool  IsGrid;

            uniform vec3  LightPos;
            uniform vec3  ViewPos;
            uniform float shininess;
            uniform vec3  SpecularColor;
            uniform vec3  LightColor;

            in vec3 vertexColor;
            in vec3 vWorldPos;
            in vec3 vNormal;

            out vec4 FragColor;

            void main() {

                if (IsGrid) {
                    FragColor = vec4(vertexColor, 1.0);
                    return;
                }

                vec3 N = normalize(vNormal);
                vec3 L = normalize(LightPos - vWorldPos);

                float NdotL = max(dot(N, L), 0.0);

                vec3 diffuse = NdotL * vertexColor;
                vec3 ambient = 0.1 * vertexColor;

                vec3 color = LightColor * (ambient + diffuse);

                FragColor = vec4(color, 1.0);
            }";

        //GPU handles 
        private int _program;
        private int _vao, _vbo;

        private int uProjection, uModel, uView, uIsGrid, uLightColor;

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

        private bool _showGrid = true;

        private Vector3 _lightPos = new Vector3(0f, 3f, 3f);

        //mouse control
        private bool _dragging = false;
        private Point _lastMouse;
        private float _yaw = 0f;
        private float _pitch = 0f;
        private float _distance = -5f;

        private enum ShapeType { Triangle, Quad, Box, SubdividedBox, Cylinder, Flade }
        private ShapeType _activeShape = ShapeType.Triangle;

        //UI controls
        private TrackBar _tbTwist, _tbBend, _tbBulge;
        private Label _lblTwist, _lblBend, _lblBulge;
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
                var l = new Label { Text = text, Left = 12, Top = top, Width = 220, ForeColor = Color.LightBlue, BackColor = Color.Transparent };
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

            int y = 20;

            //figurvælger
            var lblShape = MakeLbl("Shape:", y);
            y += 36;

            var shapeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 12,
                Top = y,
                Width = panel.Width - 24
            };
            shapeCombo.Items.AddRange(new object[] { "Triangle", "Quad", "Box", "Subdivided Box", "Cylinder", "Flade" });
            shapeCombo.SelectedIndex = (int)_activeShape;
            shapeCombo.SelectedIndexChanged += (s, e) =>
            {
                _activeShape = (ShapeType)shapeCombo.SelectedIndex;
                RebuildModelAndUpload();
                glControl1.Invalidate();
            };
            panel.Controls.Add(shapeCombo);

            y += 40;

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

        }

        #region load, resize, paint
        private void Gl_Load(object sender, EventArgs e)
        {
            glControl1.MakeCurrent();

            GL.ClearColor(0.3f, 0.3f, 0.6f, 1.0f);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);

            _program = BuildProgram(VertexSrc, FragmentSrc);
            GL.UseProgram(_program);

            int uLightPos = GL.GetUniformLocation(_program, "LightPos");
            int uShiny = GL.GetUniformLocation(_program, "shininess");
            int uSpecCol = GL.GetUniformLocation(_program, "SpecularColor");
            uLightColor = GL.GetUniformLocation(_program, "LightColor");

            if (uLightPos >= 0) GL.Uniform3(uLightPos, new Vector3(0f, 3f, 3f)); 
            if (uShiny >= 0) GL.Uniform1(uShiny, 32f);
            if (uSpecCol >= 0) GL.Uniform3(uSpecCol, new Vector3(1f, 1f, 1f));
            if (uLightColor >= 0) GL.Uniform3(uLightColor, new Vector3(1f, 1f, 1f)); 

            uProjection = GL.GetUniformLocation(_program, "Projection");
            uModel = GL.GetUniformLocation(_program, "Model");
            uView = GL.GetUniformLocation(_program, "View");
            uIsGrid = GL.GetUniformLocation(_program, "IsGrid");


            Model = Matrix4.Identity;
            GL.UniformMatrix4(uModel, false, ref Model);
            UploadProjection();


            _distance = -5f;
            _yaw = 0f; _pitch = 0f;
            UploadView();

            Scale = Vector3.One;
            Angle = new Vector4(0, 0, 0, 1);
            _translation = Vector3.Zero;
            UpdateModelMatrix();

            verticesModel.Clear();
            verticesGround.Clear();

            //grid + pynt
            CreateGroundGrid(width: 100, depth: 100, divX: 100, divZ: 100, yOffset: -1f);
            CreateMountainGrid(-0f, -0.4f, -40f, 35f, 30f, 30, 20, new float[] { 1f, 0f, 1f });
            CreateMountainGrid(-10f, -0.6f, -40f, 15f, 10f, 20, 10, new float[] { 1f, 1f, 0f });
            CreateMountainGrid(20f, -0.5f, -40f, 28f, 22f, 20, 20, new float[] { 0f, 1f, 1f });
            CreateSynthwaveSun(0.0f, -0.6f, -40.0f, 10f, 70, new float[] { 0.7f, 0.0f, 0.0f }, new float[] { 1.0f, 1.0f, 0.0f });

            //model
            CreateTriangle(1.0f, 1.0f, 1.0f);

            CreateLightMarker(1.0f);

            groundVertexCount = verticesGround.Count / 11;
            modelVertexCount = verticesModel.Count / 11;

            var all = new float[verticesGround.Count + verticesModel.Count + verticesLight.Count];
            verticesGround.CopyTo(all, 0);
            verticesModel.CopyTo(all, verticesGround.Count);
            verticesLight.CopyTo(all, verticesGround.Count + verticesModel.Count);

            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, all.Length * sizeof(float), all, BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0); //position
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, STRIDE, 0);

            GL.EnableVertexAttribArray(1); //color
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, STRIDE, 3 * sizeof(float));

            GL.EnableVertexAttribArray(2); //uv (ikke brugt, men bevares i layout)
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

            if (lightVertexCount > 0)
            {
                GL.Uniform1(uIsGrid, 1); 
                var markerModel = Matrix4.CreateScale(0.1f) * Matrix4.CreateTranslation(_lightPos);
                GL.UniformMatrix4(uModel, false, ref markerModel);

                int start = groundVertexCount + modelVertexCount;
                GL.DrawArrays(PrimitiveType.Triangles, start, lightVertexCount);

                GL.Uniform1(uIsGrid, 0);
                GL.UniformMatrix4(uModel, false, ref Model);
            }

            if (modelVertexCount > 0)
                GL.DrawArrays(PrimitiveType.Triangles, groundVertexCount, modelVertexCount);

            GL.BindVertexArray(0);
            glControl1.SwapBuffers();
            glControl1.Invalidate();
        }
        #endregion

        private void UpdateModelMatrix()
        {
            var qx = Quaternion.FromAxisAngle(Vector3.UnitX, Angle.X);
            var qy = Quaternion.FromAxisAngle(Vector3.UnitY, Angle.Y);
            var qz = Quaternion.FromAxisAngle(Vector3.UnitZ, Angle.Z);
            var q = qz * qy * qx;

            var S = Matrix4.CreateScale(Scale);
            var R = Matrix4.CreateFromQuaternion(q);
            var T = Matrix4.CreateTranslation(_translation);

            Model = S * R * T; 

            GL.UniformMatrix4(uModel, false, ref Model);
            GL.UniformMatrix4(uModel, false, ref Model);
        }

        private void UploadView()
        {
            float r = MathF.Max(0.001f, -_distance);

            float cx = r * MathF.Cos(_pitch) * MathF.Sin(_yaw);
            float cy = r * MathF.Sin(_pitch);
            float cz = r * MathF.Cos(_pitch) * MathF.Cos(_yaw);

            var target = _translation;
            var eye = target + new Vector3(cx, cy, cz);
            var up = Vector3.UnitY;

            View = Matrix4.LookAt(eye, target, up);
            GL.UniformMatrix4(uView, false, ref View);

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

        //modifiers - når vi får sat modifiers ind i projektet
        private void OnModifiersChanged(object sender, EventArgs e)
        {

            glControl1.MakeCurrent();

            glControl1.Invalidate();
        }

        #region Mouse interaction
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            float step = 0.1f;
            switch (keyData)
            {
                case Keys.W: _translation.Z -= step; break;
                case Keys.S: _translation.Z += step; break;
                case Keys.A: _translation.X -= step; break;
                case Keys.D: _translation.X += step; break;
                case Keys.Q: _translation.Y -= step; break;
                case Keys.E: _translation.Y += step; break;
                case Keys.Left: Angle.Y -= 0.05f; break;
                case Keys.Right: Angle.Y += 0.05f; break;
                case Keys.Up: Angle.X -= 0.05f; break;
                case Keys.Down: Angle.X += 0.05f; break;
                case Keys.PageUp: Scale *= 1.05f; break;
                case Keys.PageDown: Scale *= 0.95f; break;
                default: return base.ProcessCmdKey(ref msg, keyData);
            }
            UpdateModelMatrix();
            UploadView();
            glControl1.Invalidate();
            return true;
        }

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
                case ShapeType.Flade:
                    CreateFlade(0.5f, 0.5f, 0.5f);
                    break;
            }

            modelVertexCount = verticesModel.Count / 11;

            var all = new float[verticesGround.Count + verticesModel.Count + verticesLight.Count];
            verticesGround.CopyTo(all, 0);
            verticesModel.CopyTo(all, verticesGround.Count);
            verticesLight.CopyTo(all, verticesGround.Count + verticesModel.Count);

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
            float w = width * 0.5f, h = height * 0.5f, d = depth * 0.5f;

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

        private void CreateFlade(float width, float height, float depth)
        {
            float w = width * 10.0f, h = height * 0.1f, d = depth * 10.0f;

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


        //lyskilden - fra scratch (undersøg forbedringsmuligheder)
        private List<float> verticesLight = new List<float>();
        private int lightVertexCount = 0;
        private void CreateLightMarker(float size)
        {
            verticesLight.Clear();

            float w = size * 0.5f, h = w, d = w;
            float r = 1f, g = 1f, b = 0f;

            void AddTri(float ax, float ay, float az, float ar, float ag, float ab, float au, float av,
                        float bx, float by, float bz, float br, float bg, float bb, float bu, float bv,
                        float cx, float cy, float cz, float cr, float cg, float cb, float cu, float cv,
                        float nx, float ny, float nz)
            {
                verticesLight.AddRange(new[] { ax, ay, az, ar, ag, ab, au, av, nx, ny, nz });
                verticesLight.AddRange(new[] { bx, by, bz, br, bg, bb, bu, bv, nx, ny, nz });
                verticesLight.AddRange(new[] { cx, cy, cz, cr, cg, cb, cu, cv, nx, ny, nz });
            }

            void AddQuad(float x1, float y1, float z1, float x2, float y2, float z2,
                         float x3, float y3, float z3, float x4, float y4, float z4,
                         float nx, float ny, float nz)
            {
                AddTri(x1, y1, z1, r, g, b, 0, 0, x3, y3, z3, r, g, b, 1, 1, x2, y2, z2, r, g, b, 1, 0, nx, ny, nz);
                AddTri(x1, y1, z1, r, g, b, 0, 0, x4, y4, z4, r, g, b, 0, 1, x3, y3, z3, r, g, b, 1, 1, nx, ny, nz);
            }

            AddQuad(-w, -h, d, -w, h, d, w, h, d, w, -h, d, 0, 0, 1);
            AddQuad(w, -h, -d, w, h, -d, -w, h, -d, -w, -h, -d, 0, 0, -1);
            AddQuad(-w, h, d, -w, h, -d, w, h, -d, w, h, d, 0, 1, 0);
            AddQuad(-w, -h, -d, -w, -h, d, w, -h, d, w, -h, -d, 0, -1, 0);
            AddQuad(-w, -h, -d, -w, h, -d, -w, h, d, -w, -h, d, -1, 0, 0);
            AddQuad(w, -h, d, w, h, d, w, h, -d, w, -h, -d, 1, 0, 0);

            lightVertexCount = verticesLight.Count / 11;
        }


        #endregion

        

        #region grid og pynt
        private void AddGridLine(float x1, float y1, float z1, float x2, float y2, float z2, float r, float g, float b)
        {
            _activeGround = true;
            //UV og normal fyldes med dummy (UV=0, normal=(0,1,0))
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


        //setters
        private Vector3 _translation = Vector3.Zero;

        public void SetTranslation(float tx, float ty, float tz)
        {
            _translation = new Vector3(tx, ty, tz);
            UpdateModelMatrix();
            UploadView();
            glControl1.Invalidate();
        }
        public void SetScale(float sx, float sy, float sz)
        {
            Scale = new Vector3(sx, sy, sz);
            UpdateModelMatrix();
        }
        public void SetAngles(float ax, float ay, float az)
        {
            Angle = new Vector4(ax, ay, az, 1);
            UpdateModelMatrix();
        }

        //helpers 
        private static Vector3 Normalize(float x, float y, float z)
        {
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len <= 1e-6f) return Vector3.Zero;
            return new Vector3(x / len, y / len, z / len);
        }
    }
}
