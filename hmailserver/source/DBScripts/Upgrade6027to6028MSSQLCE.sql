create table hm_metricsamples
(
	metricsampleid int identity(1,1) not null,
	metricsampletime datetime not null,
	metricsamplename nvarchar(64) not null,
	metricsamplevalue float not null
)

ALTER TABLE hm_metricsamples ADD CONSTRAINT hm_metricsamples_pk PRIMARY KEY (metricsampleid)

CREATE INDEX idx_hm_metricsamples_name_time ON hm_metricsamples (metricsamplename, metricsampletime)

update hm_dbversion set value = 6028
